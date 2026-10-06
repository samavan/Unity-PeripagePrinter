using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PeripagePrinter.Runtime
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
        #region Public State

        [field: SerializeField] public List<PeripageDiscoveredDevice> Devices { get; private set; } = new List<PeripageDiscoveredDevice>();
        [field: SerializeField] public string SelectedAddress { get; private set; }
        [field: SerializeField] public bool IsScanning { get; private set; }
        [field: SerializeField] public bool IsConnected { get; private set; }

        #endregion

        #region Inspector

        [Header("Connect")]
        [Tooltip("If the native plugin never reports connected/failed within this " +
                 "many seconds, OnConnectFailed is raised so the UI can never get " +
                 "stuck on \"Connecting...\" forever.")]
        [SerializeField] private float connectTimeoutSeconds = 60f;

        #endregion

        #region Events

        public event Action OnDevicesUpdated;
        public event Action OnConnected;
        public event Action<string> OnConnectFailed;

        #endregion

#if UNITY_ANDROID

        #region Private Fields

        private bool _subscribed;
        private Coroutine _timeout;

        #endregion

        #region Unity Lifecycle

        private void OnEnable()
        {
            // May still be null here if PeripagePrinterManager.Awake hasn't run
            // yet — that's fine, EnsureSubscribed() is retried from StartScan()
            // and ConfirmAndConnect().
            EnsureSubscribed();
        }

        private void OnDisable()
        {
            StopTimeout();

            if (_subscribed && PeripagePrinterManager.Instance != null)
            {
                PeripagePrinterManager.Instance.OnConnected -= HandleManagerConnected;
                PeripagePrinterManager.Instance.OnConnectFailed -= HandleManagerConnectFailed;
            }
            _subscribed = false;
        }

        #endregion

        #region Manager Subscription

        /// <summary>
        /// PeripagePrinterManager owns the actual AndroidJavaObject/connection —
        /// we just listen in, so calling Connect() (routed through the manager)
        /// keeps our IsConnected/events in sync with the real state. Lazy and
        /// idempotent: safe to call repeatedly, and covers the case where this
        /// component was enabled before the manager's Awake set Instance.
        /// </summary>
        private bool EnsureSubscribed()
        {
            if (_subscribed) return true;

            var manager = PeripagePrinterManager.Instance;
            if (manager == null) return false;

            manager.OnConnected += HandleManagerConnected;
            manager.OnConnectFailed += HandleManagerConnectFailed;
            _subscribed = true;
            return true;
        }

        private void HandleManagerConnected()
        {
            StopTimeout();
            IsConnected = true;
            OnConnected?.Invoke();
        }

        private void HandleManagerConnectFailed(string error)
        {
            StopTimeout();
            IsConnected = false;
            OnConnectFailed?.Invoke(error);
        }

        #endregion

        #region Discovery (Paired Device List)

        /// <summary>
        /// "Scan" on Android = re-read the OS's paired-device list. Reads it
        /// via PeripagePrinterManager.GetBondedDevicesNative() — direct
        /// android.bluetooth.BluetoothAdapter calls made from C# — rather
        /// than GetPairedPrinters() (which routes through the native .aar
        /// plugin's own getPairedPrinters(), whose unconditional
        /// BLUETOOTH_CONNECT check always fails on pre-Android-12 hardware,
        /// since that permission doesn't exist there at all). Entries come
        /// back as "Name|MAC" either way, so ConfirmAndConnect() below is
        /// unaffected — it still hands the selected MAC to
        /// PeripagePrinterManager.Connect(), which goes through the real
        /// native plugin's connect() to actually open the socket.
        /// </summary>
        [ContextMenu("1. Refresh Paired Devices")]
        public void StartScan()
        {
            EnsureSubscribed();

            if (PeripagePrinterManager.Instance == null)
            {
                Debug.LogWarning("[PeripageAndroidDeviceDiscovery] No PeripagePrinterManager in scene yet.");
                return;
            }

            IsScanning = true;
            Devices.Clear();

            string[] paired = PeripagePrinterManager.Instance.GetBondedDevicesNative();
            foreach (var entry in paired)
            {
                int sep = entry.IndexOf('|');
                if (sep < 0) continue;
                Devices.Add(new PeripageDiscoveredDevice(
                    entry.Substring(0, sep),
                    entry.Substring(sep + 1)));
            }

            IsScanning = false;
            Debug.Log($"[PeripageAndroidDeviceDiscovery] Found {Devices.Count} paired device(s).");
            OnDevicesUpdated?.Invoke();
        }

        /// <summary>Same as StartScan, named for UI clarity (e.g. a "Refresh" button).</summary>
        public void Refresh() => StartScan();

        #endregion

        #region Selection & Connection

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
                OnConnectFailed?.Invoke("PeripagePrinterManager not found");
                return;
            }

            EnsureSubscribed();

            Debug.Log($"[PeripageAndroidDeviceDiscovery] Connecting to {SelectedAddress}...");

            // Safety net: if the native side never calls back, don't leave the UI hanging.
            StopTimeout();
            _timeout = StartCoroutine(ConnectTimeout(connectTimeoutSeconds));

            // SelectedAddress is a real MAC (from the bonded-device list), so this
            // hits PeripageAndroidBridge's direct-MAC path, skipping the
            // name-matching fallback entirely.
            PeripagePrinterManager.Instance.Connect(SelectedAddress);
        }

        public void Disconnect()
        {
            StopTimeout();
            PeripagePrinterManager.Instance?.Disconnect();
            IsConnected = false;
        }

        #endregion

        #region Connect Timeout

        private void StopTimeout()
        {
            if (_timeout != null)
            {
                StopCoroutine(_timeout);
                _timeout = null;
            }
        }

        private IEnumerator ConnectTimeout(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            _timeout = null;

            if (!IsConnected)
            {
                Debug.LogWarning("[PeripageAndroidDeviceDiscovery] Connect timed out — no callback received from native plugin.");
                OnConnectFailed?.Invoke("timed out waiting for printer");
            }
        }

        #endregion

#else

        #region Non-Android Stubs

        // Non-Android platforms: no-op stubs so other scripts can reference
        // this class without needing platform #if guards everywhere.
        public void StartScan() => Debug.LogWarning("PeripageAndroidDeviceDiscovery is Android-only.");
        public void Refresh() => StartScan();
        public void SelectDevice(string address) { }
        public void ConfirmAndConnect() { }
        public void Disconnect() { }

        #endregion

#endif
    }
}