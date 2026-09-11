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
        using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        {
            var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            _bridge = new AndroidJavaObject("com.kiosk.peripage.PeripageBridge", activity);
            _bridge.Call("init", unityGameObjectName);
        }
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
            string[] paired = _bridge.Call<string[]>("getPairedPrinters");
            foreach (var entry in paired)
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