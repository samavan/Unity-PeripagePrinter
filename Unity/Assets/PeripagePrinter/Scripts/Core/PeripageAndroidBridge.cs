using System;
using UnityEngine;

namespace TechArt.Module.Peripage
{
    public class PeripageAndroidBridge : IPeripageBridge
    {
        private readonly AndroidJavaObject _bridge;
        private readonly PeripagePrinterManager _manager;

        public PeripageAndroidBridge(string unityGameObjectName, PeripagePrinterManager manager)
        {
            _manager = manager;
#if UNITY_ANDROID && !UNITY_EDITOR
    try
    {
        Debug.Log("[PeripageAndroidBridge] Creating native bridge...");
        using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        {
            var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            Debug.Log($"[PeripageAndroidBridge] currentActivity: {(activity != null ? "OK" : "NULL")}");
            _bridge = new AndroidJavaObject("com.kiosk.peripage.PeripageBridge", activity);
            Debug.Log("[PeripageAndroidBridge] Native object constructed.");
            _bridge.Call("init", unityGameObjectName);
            Debug.Log($"[PeripageAndroidBridge] init('{unityGameObjectName}') called.");
        }
    }
    catch (Exception e)
    {
        Debug.LogError($"[PeripageAndroidBridge] FAILED to create/init native bridge: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
    }
#else
            Debug.LogWarning("[PeripageAndroidBridge] Not an Android device build — _bridge stays null.");
#endif
        }

        public void Connect(string macAddressOrName)
        {
            if (_bridge == null) return;

            if (LooksLikeMacAddress(macAddressOrName))
            {
                _bridge.Call("connect", macAddressOrName);
                return;
            }

            // Kiosk printer is pre-paired once via Android Bluetooth settings;
            // PeripagePrinterManager configures it by broadcast name (human
            // readable), so resolve that to a MAC via the paired-device list.
            foreach (var entry in GetPairedPrinters())
            {
                int sep = entry.IndexOf('|');
                if (sep < 0) continue;
                string name = entry.Substring(0, sep);
                string mac = entry.Substring(sep + 1);
                if (name.IndexOf(macAddressOrName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _bridge.Call("connect", mac);
                    return;
                }
            }

            Debug.LogWarning($"[PeripageAndroidBridge] No paired device matching '{macAddressOrName}'. " +
                              "Pair it once in Android Bluetooth settings first.");
            _manager?.OnConnectFailedCallback($"no paired device matching '{macAddressOrName}'");
        }

        /// <summary>
        /// Raw "Name|MAC" entries for every Bluetooth device already paired
        /// via Android's OS Bluetooth settings. Used internally by Connect()'s
        /// name-matching fallback above, and externally by
        /// PeripageAndroidDeviceDiscovery to show a pick list — Android has no
        /// live-scan discovery UI of its own; kiosks pair once via OS settings
        /// instead, same underlying connection either way.
        /// </summary>
        public string[] GetPairedPrinters()
        {
            if (_bridge == null)
            {
                Debug.LogWarning("[PeripageAndroidBridge] GetPairedPrinters: _bridge is null (see constructor logs above).");
                return Array.Empty<string>();
            }

#if UNITY_ANDROID && !UNITY_EDITOR
    bool hasConnect = UnityEngine.Android.Permission.HasUserAuthorizedPermission("android.permission.BLUETOOTH_CONNECT");
    Debug.Log($"[PeripageAndroidBridge] GetPairedPrinters: BLUETOOTH_CONNECT granted = {hasConnect}");
#endif

            try
            {
                string[] result = _bridge.Call<string[]>("getPairedPrinters");
                Debug.Log($"[PeripageAndroidBridge] getPairedPrinters() returned {result.Length} entries: [{string.Join(", ", result)}]");
                return result;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PeripageAndroidBridge] getPairedPrinters() threw: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                return Array.Empty<string>();
            }
        }

        private static bool LooksLikeMacAddress(string s) =>
            System.Text.RegularExpressions.Regex.IsMatch(s, @"^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$");

        public void Disconnect() => _bridge?.Call("disconnect");
        public bool IsConnected() => _bridge != null && _bridge.Call<bool>("isConnected");

        public void PrintBitmap(byte[] pngBytes)
        {
            bool decoded = PeripageBitmapProtocol.TryConvertPngToPackedBitmap(
                pngBytes, out byte[] packed, out int width, out int height);

            if (!decoded)
            {
                _manager?.OnPrintFailedCallback("failed to decode image (android)");
                return;
            }

            byte[] header = PeripageBitmapProtocol.BuildRasterHeader(width, height);
            byte[] framed = new byte[
                PeripageBitmapProtocol.PRINT_PREFIX.Length +
                PeripageBitmapProtocol.PRINT_PADDING.Length +
                header.Length + packed.Length +
                PeripageBitmapProtocol.PRINT_FOOTER.Length];

            int o = 0;
            System.Array.Copy(PeripageBitmapProtocol.PRINT_PREFIX, 0, framed, o, PeripageBitmapProtocol.PRINT_PREFIX.Length); o += PeripageBitmapProtocol.PRINT_PREFIX.Length;
            System.Array.Copy(PeripageBitmapProtocol.PRINT_PADDING, 0, framed, o, PeripageBitmapProtocol.PRINT_PADDING.Length); o += PeripageBitmapProtocol.PRINT_PADDING.Length;
            System.Array.Copy(header, 0, framed, o, header.Length); o += header.Length;
            System.Array.Copy(packed, 0, framed, o, packed.Length); o += packed.Length;
            System.Array.Copy(PeripageBitmapProtocol.PRINT_FOOTER, 0, framed, o, PeripageBitmapProtocol.PRINT_FOOTER.Length);

            _bridge?.Call("printRaw", framed);
        }
    }
}
