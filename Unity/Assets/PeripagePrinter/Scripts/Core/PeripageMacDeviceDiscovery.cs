using System;
using System.Collections.Generic;
using UnityEngine;

namespace TechArt.Module.Peripage
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
    /// device list stays empty and Connect() no-ops; use the Android path
    /// (PeripagePrinterManager + PeripageAndroidBridge) for the real device.
    /// </summary>
    public class PeripageMacDeviceDiscovery : MonoBehaviour
    {
        [Serializable]
        public class DiscoveredDevice
        {
            public string name;
            public string address;
        }

        public List<DiscoveredDevice> devices = new List<DiscoveredDevice>();
        public string selectedAddress;
        public bool isScanning;
        public bool isConnected;

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
            if (isScanning && scanningNow != isScanning)
            {
                // Scan just finished
            }
            isScanning = scanningNow;

            RefreshDeviceListFromNative();

            bool connectedNow = PeripageMacNative.Peripage_IsConnected() == 1;
            if (connectedNow && !isConnected)
            {
                isConnected = true;
                OnConnected?.Invoke();
            }
            else if (!connectedNow && isConnected)
            {
                isConnected = false;
            }

            if (PeripageMacNative.Peripage_LastConnectFailed() == 1 && !isConnected)
            {
                OnConnectFailed?.Invoke("Failed to connect to printer");
            }
        }

        private void RefreshDeviceListFromNative()
        {
            int count = PeripageMacNative.Peripage_GetDeviceCount();
            if (count == devices.Count) return; // cheap check to avoid rebuilding every poll

            devices.Clear();
            for (int i = 0; i < count; i++)
            {
                devices.Add(new DiscoveredDevice
                {
                    name = PeripageMacNative.GetDeviceName(i),
                    address = PeripageMacNative.GetDeviceAddress(i)
                });
            }
            Debug.Log($"[PeripageMacDeviceDiscovery] Found {count} device(s):");
            foreach (var d in devices)
            {
                Debug.Log($"  - {d.name} ({d.address})");
            }
            OnDevicesUpdated?.Invoke();
        }

        /// <summary>Step 1/2: begin (or restart) scanning for nearby devices.</summary>
        [ContextMenu("1. Start Scan")]
        public void StartScan()
        {
            devices.Clear();
            selectedAddress = null;
            PeripageMacNative.Peripage_StartScan();
            isScanning = true;
        }

        /// <summary>Step 2: explicit refresh button — same as StartScan, named for UI clarity.</summary>
        public void Refresh() => StartScan();

        /// <summary>Step 3: called when the user taps/clicks a device in the list.</summary>
        public void SelectDevice(string address)
        {
            selectedAddress = address;
        }

        /// <summary>Steps 4/5: user pressed OK — connect to the selected device.</summary>
        [ContextMenu("3. Connect To First Found Device")]
        public void ConnectToFirstFoundDevice()
        {
            if (devices.Count == 0)
            {
                Debug.LogWarning("No devices found yet — run Start Scan first and wait a few seconds.");
                return;
            }
            SelectDevice(devices[0].address);
            Debug.Log($"Connecting to {devices[0].name} ({devices[0].address})...");
            ConfirmAndConnect();
        }

        public void ConfirmAndConnect()
        {
            if (string.IsNullOrEmpty(selectedAddress))
            {
                OnConnectFailed?.Invoke("No device selected");
                return;
            }
            PeripageMacNative.Peripage_Connect(selectedAddress);
        }

        public void Disconnect()
        {
            PeripageMacNative.Peripage_Disconnect();
            isConnected = false;
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
