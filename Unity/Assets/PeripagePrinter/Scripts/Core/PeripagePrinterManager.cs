using System;
using System.IO;
using UnityEngine;
using UnityEngine.Android;

namespace TechArt.Module.Peripage
{
    /// <summary>
    /// Attach this to a GameObject named "PeripageManager" in your kiosk scene
    /// (the name must match what you pass into PeripageBridge.init(), and what
    /// UnitySendMessage targets from the Kotlin side).
    ///
    /// Handles: runtime permissions, connecting to the printer, converting a
    /// Texture2D into the 1-bit packed bitmap format the printer needs, and
    /// firing the print call.
    ///
    /// Works in TWO modes, switched automatically by platform:
    ///   - On Android device: talks to the real Bluetooth printer via
    ///     PeripageAndroidBridge (AndroidJavaObject -> Kotlin plugin).
    ///   - In the Editor (and non-Android standalone builds): talks to
    ///     PeripageEditorMockBridge, which simulates connect/print timing and
    ///     outcomes, so you can build/test the kiosk UI flow on Win/Mac without
    ///     the physical printer or an Android build.
    /// </summary>
    public class PeripagePrinterManager : MonoBehaviour
    {
        public static PeripagePrinterManager Instance { get; private set; }

        [Header("Printer")]
        [Tooltip("Bluetooth broadcast name of the kiosk's paired Peripage printer " +
                 "(e.g. \"PPG_P21_XXXX\") — NOT a MAC address. Find it in Android " +
                 "Bluetooth settings after pairing once. Must match the name your " +
                 "forked BluetoothHelper library is configured to look for.")]
        public string printerName = "PPG_P21_XXXX";

        [Header("Editor / Standalone testing")]
        [Tooltip("When running outside Android (Editor, Win/Mac standalone), " +
                 "use the mock bridge instead of trying (and failing) to reach " +
                 "real Bluetooth hardware.")]
        public bool useMockBridgeOutsideAndroid = true;

        [Tooltip("If true, also saves a PNG preview of what would have been " +
                 "printed to a 'PeripagePreviews' folder next to the project, " +
                 "so you can eyeball the monochrome conversion without hardware.")]
        public bool saveMockPrintPreviewPng = true;

        public bool simulateConnectFailureInEditor = false;
        public bool simulatePrintFailureInEditor = false;

        [Header("DEBUG: Mac protocol diagnostic")]
        [Tooltip("TEMPORARY: when on Mac and this is checked, PrintPhoto() ignores " +
                 "the real image entirely and sends a single tiny text-only test " +
                 "payload instead — no PNG decode, no chunking. Used to isolate " +
                 "whether the native layer itself is slow, or if it's specific to " +
                 "the full raster print path. Uncheck to restore normal printing.")]
        public bool debugTinyTestPrintOnly = false;

        public const int PRINTER_WIDTH_PX = 384;

        public event Action OnConnected;
        public event Action<string> OnConnectFailed;
        public event Action OnPrintComplete;
        public event Action<string> OnPrintFailed;

        private IPeripageBridge _bridge;
        private bool _initialized;

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            RequestBluetoothPermissions();
        }

        private void RequestBluetoothPermissions()
        {
    #if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission("android.permission.BLUETOOTH_CONNECT"))
            {
                Permission.RequestUserPermission("android.permission.BLUETOOTH_CONNECT");
            }
            if (!Permission.HasUserAuthorizedPermission("android.permission.BLUETOOTH_SCAN"))
            {
                Permission.RequestUserPermission("android.permission.BLUETOOTH_SCAN");
            }
            // On a kiosk, permissions can also just be granted once at setup time
            // via adb, so you don't have to handle the prompt UI at runtime:
            //   adb shell pm grant <package> android.permission.BLUETOOTH_CONNECT
            //   adb shell pm grant <package> android.permission.BLUETOOTH_SCAN
    #endif
            InitBridge();
        }

        private void InitBridge()
        {
    #if UNITY_ANDROID && !UNITY_EDITOR
            _bridge = new PeripageAndroidBridge(gameObject.name);
            _initialized = true;
    #elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            // Real Mac path: talks to the same PeripageMacNative plugin that
            // PeripageMacDeviceDiscovery uses to scan/connect, so whatever the
            // user already connected to via that flow is what Print() uses here.
            _bridge = new PeripageMacBridge(this);
            _initialized = true;
            Debug.Log("[PeripagePrinterManager] Using real Mac Bluetooth bridge (PeripageMacNative). " +
                      "Connect via PeripageMacDeviceDiscovery's scan/select/OK flow before printing.");
    #else
            if (useMockBridgeOutsideAndroid)
            {
                var mock = new PeripageEditorMockBridge(this, this)
                {
                    simulateConnectFailure = simulateConnectFailureInEditor,
                    simulatePrintFailure = simulatePrintFailureInEditor,
                };
                _bridge = mock;
                _initialized = true;
                Debug.Log("[PeripagePrinterManager] Using Editor/standalone mock bridge " +
                          "(no real printer required). Disable 'useMockBridgeOutsideAndroid' " +
                          "if you specifically want to test the real-device code path.");
            }
            else
            {
                Debug.LogWarning("[PeripagePrinterManager] Not on Android and mock bridge " +
                                  "disabled — Connect()/PrintPhoto() calls will no-op.");
                _initialized = false;
            }
    #endif
        }

        public void Connect()
        {
            if (!_initialized) { Debug.LogWarning("Bridge not initialized yet"); return; }
            _bridge.Connect(printerName);
        }

        public void Disconnect()
        {
            if (!_initialized) return;
            _bridge.Disconnect();
        }

        public bool IsConnected()
        {
            if (!_initialized) return false;
            return _bridge.IsConnected();
        }

        /// <summary>
        /// Converts a Texture2D (e.g. the kiosk photo) into the packed 1-bit
        /// bitmap format and sends it to the printer. Handles resizing to the
        /// printer's native width and basic thresholding to monochrome.
        /// </summary>
        public void PrintPhoto(Texture2D source)
        {
            if (!_initialized || _bridge == null)
            {
                OnPrintFailed?.Invoke("Bridge not initialized");
                return;
            }

    #if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            if (debugTinyTestPrintOnly && _bridge is PeripageMacBridge macBridge)
            {
                Debug.Log("[PeripagePrinterManager] debugTinyTestPrintOnly is ON — " +
                          "sending a tiny text test instead of the real image.");
                bool ok = macBridge.PrintTestText("HELLO");
                if (ok) OnPrintCompleteCallback("");
                else OnPrintFailedCallback("tiny test print failed (mac native)");
                return;
            }
    #endif

            // Resize to the printer's native width — the library handles the
            // actual monochrome/dithering conversion internally, we just need
            // to hand it a reasonably-sized image rather than a huge photo.
            Texture2D resized = ResizeToPrinterWidth(source, PRINTER_WIDTH_PX);
            byte[] pngBytes = resized.EncodeToPNG();

    #if UNITY_EDITOR
            if (saveMockPrintPreviewPng && _bridge is PeripageEditorMockBridge)
            {
                SavePreviewPng(resized);
            }
    #endif

            _bridge.PrintBitmap(pngBytes);

            if (resized != source) Destroy(resized);
        }

    #if UNITY_EDITOR
        /// <summary>
        /// Editor-only convenience: writes out what the printer would have
        /// received as a viewable PNG, so you can sanity-check the
        /// resize/monochrome conversion without any hardware attached.
        /// </summary>
        private void SavePreviewPng(Texture2D resized)
        {
            try
            {
                string folder = Path.Combine(Application.dataPath, "..", "PeripagePreviews");
                Directory.CreateDirectory(folder);
                string filename = $"print_preview_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string path = Path.Combine(folder, filename);
                File.WriteAllBytes(path, resized.EncodeToPNG());
                Debug.Log($"[PeripagePrinterManager] Saved print preview: {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to save print preview: {e.Message}");
            }
        }
    #endif

        // ---------- Image conversion ----------

        private Texture2D ResizeToPrinterWidth(Texture2D source, int targetWidth)
        {
            if (source.width == targetWidth) return source;

            int targetHeight = Mathf.RoundToInt(source.height * (targetWidth / (float)source.width));
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight);
            Graphics.Blit(source, rt);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D resized = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
            resized.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            resized.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            return resized;
        }

        // ---------- Callbacks invoked by Kotlin via UnitySendMessage ----------
        // Method names below must exactly match what PeripageBridge.sendToUnity() calls.

        public void OnConnectedCallback(string macAddress)
        {
            Debug.Log($"Printer connected: {macAddress}");
            OnConnected?.Invoke();
        }

        public void OnConnectFailedCallback(string error)
        {
            Debug.LogWarning($"Printer connect failed: {error}");
            OnConnectFailed?.Invoke(error);
        }

        public void OnDisconnectedCallback(string _)
        {
            Debug.Log("Printer disconnected");
        }

        public void OnPrintCompleteCallback(string _)
        {
            Debug.Log("Print complete");
            OnPrintComplete?.Invoke();
        }

        public void OnPrintFailedCallback(string error)
        {
            Debug.LogWarning($"Print failed: {error}");
            OnPrintFailed?.Invoke(error);
        }
    }
}
