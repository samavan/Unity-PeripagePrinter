using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeripagePrinter.Runtime
{
    /// <summary>
    /// Implements the requested flow for Mac testing:
    ///   1. Show list of every Bluetooth device found
    ///   2. Refresh button for devices that showed up late
    ///   3. User selects a device from the list
    ///   4. User presses OK to confirm the selection
    ///   5. Connect to the printer
    ///
    /// Only meaningful on Mac (Editor or standalone) — the native plugin this
    /// wraps is a macOS-only .bundle (IOBluetooth). On other platforms, the
    /// device list stays empty and Connect() no-ops; use
    /// PeripageAndroidDeviceDiscovery on Android instead.
    ///
    /// Implements IPeripageDeviceDiscovery so UI code (UIPeripageDiscovery)
    /// can drive this or PeripageAndroidDeviceDiscovery interchangeably.
    /// </summary>
    public class PeripageMacDeviceDiscovery : MonoBehaviour, IPeripageDeviceDiscovery
    {
        [field: SerializeField] public List<PeripageDiscoveredDevice> Devices { get; private set; } = new List<PeripageDiscoveredDevice>();
        [field: SerializeField] public string SelectedAddress { get; private set; }
        [field: SerializeField] public bool IsScanning { get; private set; }
        [field: SerializeField] public bool IsConnected { get; private set; }

        public event Action OnDevicesUpdated;
        public event Action OnConnected;
        public event Action<string> OnConnectFailed;

        private float _pollTimer;
        private const float POLL_INTERVAL = 0.5f;

    #if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX

        void Update()
        {
            // Poll rather than callback — keeps the native bridge simple.
            _pollTimer += Time.deltaTime;
            if (_pollTimer < POLL_INTERVAL) return;
            _pollTimer = 0f;

            bool scanningNow = PeripageMacNative.Peripage_IsScanning() == 1;
            if (IsScanning && scanningNow != IsScanning && !_scanFinishedLogged)
            {
                _scanFinishedLogged = true;
                Debug.Log(Devices.Count > 0
                    ? $"[PeripageMacDeviceDiscovery] Scan finished — {Devices.Count} device(s) found."
                    : "[PeripageMacDeviceDiscovery] Scan finished — no devices found.");
            }
            IsScanning = scanningNow;

            RefreshDeviceListFromNative();

            bool connectedNow = PeripageMacNative.Peripage_IsConnected() == 1;
            if (connectedNow && !IsConnected)
            {
                IsConnected = true;
                _connectedAt = Time.realtimeSinceStartup;
                _connectFailedLogged = false; // clear so a future failed attempt can log again
                Debug.Log($"[PeripageMacDeviceDiscovery] Connected to {SelectedAddress}.");
                OnConnected?.Invoke();
            }
            else if (!connectedNow && IsConnected)
            {
                IsConnected = false;
                float heldFor = Time.realtimeSinceStartup - _connectedAt;
                Debug.LogWarning($"[PeripageMacDeviceDiscovery] Connection dropped — was connected for {heldFor:F1}s.");
            }

            // Peripage_LastConnectFailed() appears to be a sticky native flag that stays
            // set until the next connect attempt, not a one-shot event — so without this
            // guard, this would re-log every single poll (every 0.5s) forever after any
            // failed attempt. Only report it once; ConfirmAndConnect()/StartScan() reset
            // the guard so the next real attempt can report its own failure.
            if (PeripageMacNative.Peripage_LastConnectFailed() == 1 && !IsConnected && !_connectFailedLogged)
            {
                _connectFailedLogged = true;
                Debug.LogWarning("[PeripageMacDeviceDiscovery] Connect failed (native reported LastConnectFailed).");
                OnConnectFailed?.Invoke("Failed to connect to printer");
            }
        }

        private float _connectedAt;
        private bool _connectFailedLogged;
        private bool _scanFinishedLogged;

        private void RefreshDeviceListFromNative()
        {
            int count = PeripageMacNative.Peripage_GetDeviceCount();
            if (count == Devices.Count) return; // cheap check to avoid rebuilding every poll

            Devices.Clear();
            for (int i = 0; i < count; i++)
            {
                Devices.Add(new PeripageDiscoveredDevice(
                    PeripageMacNative.GetDeviceName(i),
                    PeripageMacNative.GetDeviceAddress(i)));
            }
            Debug.Log($"[PeripageMacDeviceDiscovery] Found {count} device(s):");
            foreach (var d in Devices)
            {
                Debug.Log($"  - {d.name} ({d.address})");
            }
            OnDevicesUpdated?.Invoke();
        }

        /// <summary>Step 1/2: begin (or restart) scanning for nearby devices.</summary>
        [ContextMenu("1. Start Scan")]
        public void StartScan()
        {
            Devices.Clear();
            SelectedAddress = null;
            _scanFinishedLogged = false;
            Debug.Log("[PeripageMacDeviceDiscovery] Starting scan...");
            PeripageMacNative.Peripage_StartScan();
            IsScanning = true;
        }

        /// <summary>Step 2: explicit refresh button — same as StartScan, named for UI clarity.</summary>
        public void Refresh() => StartScan();

        /// <summary>Step 3: called when the user taps/clicks a device in the list.</summary>
        public void SelectDevice(string address)
        {
            SelectedAddress = address;
        }

        /// <summary>Steps 4/5: user pressed OK — connect to the selected device.</summary>
        [ContextMenu("3. Connect To First Found Device")]
        public void ConnectToFirstFoundDevice()
        {
            if (Devices.Count == 0)
            {
                Debug.LogWarning("No devices found yet — run Start Scan first and wait a few seconds.");
                return;
            }
            SelectDevice(Devices[0].address);
            Debug.Log($"Connecting to {Devices[0].name} ({Devices[0].address})...");
            ConfirmAndConnect();
        }

        public void ConfirmAndConnect()
        {
            if (string.IsNullOrEmpty(SelectedAddress))
            {
                Debug.LogWarning("[PeripageMacDeviceDiscovery] ConfirmAndConnect called with no device selected.");
                OnConnectFailed?.Invoke("No device selected");
                return;
            }
            _connectFailedLogged = false;
            Debug.Log($"[PeripageMacDeviceDiscovery] Connecting to {SelectedAddress}...");
            PeripageMacNative.Peripage_Connect(SelectedAddress);
        }

        public void Disconnect()
        {
            Debug.Log("[PeripageMacDeviceDiscovery] Disconnect requested.");
            PeripageMacNative.Peripage_Disconnect();
            IsConnected = false;
        }

        public bool SendBytes(byte[] data)
        {
            return PeripageMacNative.Peripage_SendBytes(data, data.Length) == 1;
        }

    #else
        // Non-Mac platforms: no-op stubs so other scripts can reference this
        // class without needing platform #if guards everywhere.
        public void StartScan() => Debug.LogWarning("PeripageMacDeviceDiscovery is Mac-only.");
        public void Refresh() => StartScan();
        public void SelectDevice(string address) { }
        public void ConfirmAndConnect() { }
        public void Disconnect() { }
        public bool SendBytes(byte[] data) => false;
    #endif
    }
}
