using UnityEngine;

namespace TechArt.Module.Peripage
{
    /// <summary>
    /// Real Android implementation — wraps the Kotlin PeripageBridge via
    /// AndroidJavaObject. Only ever instantiated on-device (UNITY_ANDROID &&
    /// !UNITY_EDITOR); see PeripagePrinterManager for the platform switch.
    /// </summary>
    public class PeripageAndroidBridge : IPeripageBridge
    {
        private readonly AndroidJavaObject _bridge;

        public PeripageAndroidBridge(string unityGameObjectName)
        {
    #if UNITY_ANDROID && !UNITY_EDITOR
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            {
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                _bridge = new AndroidJavaObject("com.kiosk.peripage.PeripageBridge", activity);
                _bridge.Call("init", unityGameObjectName);
            }
    #endif
        }

        public void Connect(string macAddress)
        {
            _bridge?.Call("connect", macAddress);
        }

        public void Disconnect()
        {
            _bridge?.Call("disconnect");
        }

        public bool IsConnected()
        {
            return _bridge != null && _bridge.Call<bool>("isConnected");
        }

        public void PrintBitmap(byte[] imageBytes)
        {
            _bridge?.Call("printImageBytes", imageBytes);
        }
    }
}
