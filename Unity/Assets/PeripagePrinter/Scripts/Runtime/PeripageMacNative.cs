using System.Runtime.InteropServices;

namespace PeripagePrinter.Runtime
{
    /// <summary>
    /// Raw P/Invoke bindings to PeripageMacPlugin.bundle. Only functional in
    /// the Editor on Mac or in a Mac standalone build — the DllImport calls
    /// will fail/no-op on any other platform, so callers should gate usage
    /// with #if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX.
    /// </summary>
    public static class PeripageMacNative
    {
        private const string PLUGIN_NAME = "PeripageMacPlugin";

        [DllImport(PLUGIN_NAME)] public static extern void Peripage_StartScan();
        [DllImport(PLUGIN_NAME)] public static extern void Peripage_StopScan();
        [DllImport(PLUGIN_NAME)] public static extern int Peripage_IsScanning();
        [DllImport(PLUGIN_NAME)] public static extern int Peripage_GetDeviceCount();
        [DllImport(PLUGIN_NAME)] public static extern System.IntPtr Peripage_GetDeviceName(int index);
        [DllImport(PLUGIN_NAME)] public static extern System.IntPtr Peripage_GetDeviceAddress(int index);
        [DllImport(PLUGIN_NAME)] public static extern void Peripage_Connect(string address);
        [DllImport(PLUGIN_NAME)] public static extern void Peripage_Disconnect();
        [DllImport(PLUGIN_NAME)] public static extern int Peripage_IsConnected();
        [DllImport(PLUGIN_NAME)] public static extern int Peripage_LastConnectFailed();
        [DllImport(PLUGIN_NAME)] public static extern int Peripage_SendBytes(byte[] bytes, int length);

        public static string GetDeviceName(int index) =>
            Marshal.PtrToStringAnsi(Peripage_GetDeviceName(index));

        public static string GetDeviceAddress(int index) =>
            Marshal.PtrToStringAnsi(Peripage_GetDeviceAddress(index));
    }
}
