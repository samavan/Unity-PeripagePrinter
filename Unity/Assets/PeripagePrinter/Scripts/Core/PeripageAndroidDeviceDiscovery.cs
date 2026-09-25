using System;
using System.Collections.Generic;
using UnityEngine;

namespace TechArt.Module.Peripage
{
    /// <summary>
    /// Android counterpart to PeripageMacDeviceDiscovery, but "discovery" here
    /// means something different: a kiosk's Peripage printer is paired once,
    /// up front, via Android's own Bluetooth settings — there's no in-app live
    /// scan (Android's classic Bluetooth discovery is also far slower/flakier
    /// than just reading the paired list). So StartScan()/Refresh() here just
    /// reads the OS's already-paired device list back from the native plugin.
    ///
    /// Talks through PeripagePrinterManager's existing PeripageAndroidBridge
    /// instance rather than opening a second native connection, so this and
    /// the manager's real print bridge always agree on connection state.
    ///
    /// Implements IPeripageDeviceDiscovery so UI code (UIPeripageDiscovery)
    /// can drive this or PeripageMacDeviceDiscovery interchangeably.
    /// </summary>
    public class PeripageAndroidDeviceDiscovery : MonoBehaviour, IPeripageDeviceDiscovery
    {
        [field: SerializeField] public List<PeripageDiscoveredDevice> Devices { get; private set; } = new List<PeripageDiscoveredDevice>();
        [field: SerializeField] public string SelectedAddress { get; private set; }
        [field: SerializeField] public bool IsScanning { get; private set; }
        [field: SerializeField] public bool IsConnected { get; private set; }

        public event Action OnDevicesUpdated;
        public event Action OnConnected;
        public event Action<string> OnConnectFailed;

    #if UNITY_ANDROID

        private void OnEnable()
        {
            // PeripagePrinterManager owns the actual AndroidJavaObject/connection —
            // we just listen in, so calling Connect() below (routed through the
            // manager) keeps our IsConnected/events in sync with the real state.
            if (PeripagePrinterManager.Instance != null)
            {
                PeripagePrinterManager.Instance.OnConnected += HandleManagerConnected;
                PeripagePrinterManager.Instance.OnConnectFailed += HandleManagerConnectFailed;
            }
        }

        private void OnDisable()
        {
            if (PeripagePrinterManager.Instance != null)
            {
                PeripagePrinterManager.Instance.OnConnected -= HandleManagerConnected;
                PeripagePrinterManager.Instance.OnConnectFailed -= HandleManagerConnectFailed;
            }
        }

        private void HandleManagerConnected()
        {
            IsConnected = true;
            OnConnected?.Invoke();
        }

        private void HandleManagerConnectFailed(string error)
        {
            IsConnected = false;
            OnConnectFailed?.Invoke(error);
        }

        /// <summary>
        /// "Scan" on Android = re-read the OS's paired-device list via the
        /// native plugin (fast, synchronous, local — not a live BLE/classic
        /// scan). Entries come back as "Name|MAC" from getPairedPrinters().
        /// </summary>
        [ContextMenu("1. Refresh Paired Devices")]
        public void StartScan()
        {
            if (PeripagePrinterManager.Instance == null)
            {
                Debug.LogWarning("[PeripageAndroidDeviceDiscovery] StartScan: PeripagePrinterManager.Instance is null.");
                return;
            }

            IsScanning = true;
            Devices.Clear();

            string[] paired = PeripagePrinterManager.Instance.GetPairedPrinters();
            Debug.Log($"[PeripageAndroidDeviceDiscovery] StartScan: got {paired.Length} entries from GetPairedPrinters().");

            foreach (var entry in paired)
            {
                int sep = entry.IndexOf('|');
                if (sep < 0)
                {
                    Debug.LogWarning($"[PeripageAndroidDeviceDiscovery] Skipping malformed entry (no '|'): '{entry}'");
                    continue;
                }
                Devices.Add(new PeripageDiscoveredDevice(entry.Substring(0, sep), entry.Substring(sep + 1)));
            }

            IsScanning = false;
            Debug.Log($"[PeripageAndroidDeviceDiscovery] Parsed {Devices.Count} device(s).");
            OnDevicesUpdated?.Invoke();
        }

        /// <summary>Same as StartScan, named for UI clarity (e.g. a "Refresh" button).</summary>
        public void Refresh() => StartScan();

        /// <summary>Called when the user taps a device in the list.</summary>
        public void SelectDevice(string address)
        {
            SelectedAddress = address;
        }

        /// <summary>User pressed OK — connect to the selected device.</summary>
        public void ConfirmAndConnect()
        {
            if (string.IsNullOrEmpty(SelectedAddress))
            {
                Debug.LogWarning("[PeripageAndroidDeviceDiscovery] ConfirmAndConnect called with no device selected.");
                OnConnectFailed?.Invoke("No device selected");
                return;
            }

            if (PeripagePrinterManager.Instance == null)
            {
                Debug.LogWarning("[PeripageAndroidDeviceDiscovery] No PeripagePrinterManager in scene yet.");
                return;
            }

            Debug.Log($"[PeripageAndroidDeviceDiscovery] Connecting to {SelectedAddress}...");
            // SelectedAddress is a real MAC (from getPairedPrinters), so this
            // hits PeripageAndroidBridge's direct-MAC path, skipping the
            // name-matching fallback entirely.
            PeripagePrinterManager.Instance.Connect(SelectedAddress);
        }

        public void Disconnect()
        {
            PeripagePrinterManager.Instance?.Disconnect();
            IsConnected = false;
        }

    #else
        // Non-Android platforms: no-op stubs so other scripts can reference
        // this class without needing platform #if guards everywhere.
        public void StartScan() => Debug.LogWarning("PeripageAndroidDeviceDiscovery is Android-only.");
        public void Refresh() => StartScan();
        public void SelectDevice(string address) { }
        public void ConfirmAndConnect() { }
        public void Disconnect() { }
    #endif
    }
}
