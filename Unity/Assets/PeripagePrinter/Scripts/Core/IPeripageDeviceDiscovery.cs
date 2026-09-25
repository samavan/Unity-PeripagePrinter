using System;
using System.Collections.Generic;

namespace TechArt.Module.Peripage
{
    /// <summary>
    /// A single Bluetooth device as surfaced by a discovery implementation —
    /// either freshly scanned (Mac) or read from the OS's paired-device list
    /// (Android). Shared so UI code (UIPeripageDiscovery) doesn't care which.
    /// </summary>
    [Serializable]
    public class PeripageDiscoveredDevice
    {
        public string name;
        public string address;

        public PeripageDiscoveredDevice() { }

        public PeripageDiscoveredDevice(string name, string address)
        {
            this.name = name;
            this.address = address;
        }
    }

    /// <summary>
    /// Common surface for the "list devices, refresh, select one, confirm and
    /// connect" flow, implemented per-platform:
    ///   - PeripageMacDeviceDiscovery: live IOBluetooth scan (Mac only)
    ///   - PeripageAndroidDeviceDiscovery: reads the already-paired device
    ///     list from the native Android plugin — Android kiosks pair once via
    ///     OS Bluetooth settings, so there's no in-app live scan
    ///
    /// UIPeripageDiscovery talks only to this interface, so the same UI script
    /// drives whichever concrete implementation is available on the current
    /// platform.
    /// </summary>
    public interface IPeripageDeviceDiscovery
    {
        List<PeripageDiscoveredDevice> Devices { get; }
        string SelectedAddress { get; }
        bool IsScanning { get; }
        bool IsConnected { get; }

        event Action OnDevicesUpdated;
        event Action OnConnected;
        event Action<string> OnConnectFailed;

        /// <summary>Begin (or restart) looking for devices — a live scan on Mac, a paired-list refresh on Android.</summary>
        void StartScan();

        /// <summary>Same as StartScan, named for UI clarity (e.g. a "Refresh" button).</summary>
        void Refresh();

        /// <summary>Mark a device (by address) as the pending selection, ahead of ConfirmAndConnect.</summary>
        void SelectDevice(string address);

        /// <summary>Connect to whichever device SelectDevice last set.</summary>
        void ConfirmAndConnect();

        void Disconnect();
    }
}
