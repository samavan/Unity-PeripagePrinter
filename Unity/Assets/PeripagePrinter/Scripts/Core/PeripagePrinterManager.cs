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

        [Header("Calibration")]
        [Tooltip("Vertical dot-pitch correction — see PeripageBitmapProtocol." +
                 "VerticalAspectCorrection's doc comment for what this fixes and " +
                 "how to derive it (print BuildCalibrationSquareTexture, measure " +
                 "with a ruler, feed the numbers into " +
                 "ComputeVerticalAspectCorrectionFromMeasurement). 1.0 = no " +
                 "correction. You can drag this slider live in Play Mode and " +
                 "watch UIPeripagePrintDemo's printer-quality preview update to " +
                 "match, rather than reprinting paper for every trial value. " +
                 "Persisted across sessions (PlayerPrefs) once changed here or " +
                 "via SetVerticalAspectCorrection, so a kiosk only needs " +
                 "calibrating once.")]
        [Range(0.5f, 2f)]
        public float verticalAspectCorrection = PeripageBitmapProtocol.DEFAULT_VERTICAL_ASPECT_CORRECTION;

        /// <summary>Raised whenever the calibration value changes (Inspector, PlayerPrefs load, or SetVerticalAspectCorrection), so a debug UI can keep its slider/label in sync without polling.</summary>
        public event Action<float> OnCalibrationChanged;

        private const string CalibrationPlayerPrefsKey = "Peripage_VerticalAspectCorrection";

        [Header("Dot density")]
        [Tooltip("Caps how much of the print can be black dots on average — " +
                 "see PeripageBitmapProtocol.MaxAverageDensity's doc comment. " +
                 "Lower this if prints look too dark/dirty/dotty; raise it to " +
                 "recover midtone detail. Same live-preview workflow as the " +
                 "vertical aspect slider: drag it in Play Mode and " +
                 "UIPeripagePrintDemo's printer-quality preview updates to " +
                 "match. Persisted across sessions (PlayerPrefs).")]
        [Range(0.05f, 0.9f)]
        public float maxAverageDensity = PeripageBitmapProtocol.DEFAULT_MAX_AVERAGE_DENSITY;

        /// <summary>Raised whenever maxAverageDensity changes (Inspector, PlayerPrefs load, or SetMaxAverageDensity), so a debug UI can keep its slider/label in sync without polling.</summary>
        public event Action<float> OnDensityChanged;

        private const string DensityPlayerPrefsKey = "Peripage_MaxAverageDensity";

        [Header("Cut margin")]
        [Tooltip("Blank paper fed after the image, in mm, so there's room to " +
                 "cut without slicing into the photo. Converted to print rows " +
                 "via the calibration value above, so calibrate that first — " +
                 "otherwise this comes out the wrong physical length too.")]
        public float bottomCutMarginMm = PeripageBitmapProtocol.DEFAULT_BOTTOM_CUT_MARGIN_MM;

        [Header("DEBUG: Mac protocol diagnostic")]
        [Tooltip("TEMPORARY: when on Mac and this is checked, PrintPhoto() ignores " +
                 "the real image entirely and sends a single tiny text-only test " +
                 "payload instead — no PNG decode, no chunking. Used to isolate " +
                 "whether the native layer itself is slow, or if it's specific to " +
                 "the full raster print path. Uncheck to restore normal printing.")]
        public bool debugTinyTestPrintOnly = false;

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

            // A kiosk should only need calibrating once — load whatever was
            // saved last time before anything else touches VerticalAspectCorrection.
            if (PlayerPrefs.HasKey(CalibrationPlayerPrefsKey))
            {
                verticalAspectCorrection = PlayerPrefs.GetFloat(CalibrationPlayerPrefsKey);
            }
            PeripageBitmapProtocol.VerticalAspectCorrection = verticalAspectCorrection;

            if (PlayerPrefs.HasKey(DensityPlayerPrefsKey))
            {
                maxAverageDensity = PlayerPrefs.GetFloat(DensityPlayerPrefsKey);
            }
            PeripageBitmapProtocol.MaxAverageDensity = maxAverageDensity;
        }

        void Start()
        {
            RequestBluetoothPermissions();
        }

#if UNITY_EDITOR
        // Keeps the protocol's live value in sync while dragging the Inspector
        // slider in Play Mode, without needing a UI Slider at all.
        void OnValidate()
        {
            PeripageBitmapProtocol.VerticalAspectCorrection = verticalAspectCorrection;
            PeripageBitmapProtocol.MaxAverageDensity = maxAverageDensity;
        }
#endif

        /// <summary>
        /// Bind this to a UI Slider's onValueChanged for a live calibration
        /// panel. Updates the Inspector-visible field, pushes the value into
        /// PeripageBitmapProtocol.VerticalAspectCorrection immediately (so the
        /// very next preview/print reflects it), persists it via PlayerPrefs,
        /// and raises OnCalibrationChanged so any listening UI (e.g. a value
        /// label) can update without polling.
        /// </summary>
        public void SetVerticalAspectCorrection(float value)
        {
            PeripageBitmapProtocol.VerticalAspectCorrection = value; // clamps internally (0.1–5.0)
            verticalAspectCorrection = PeripageBitmapProtocol.VerticalAspectCorrection;
            PlayerPrefs.SetFloat(CalibrationPlayerPrefsKey, verticalAspectCorrection);
            OnCalibrationChanged?.Invoke(verticalAspectCorrection);
        }

        public float GetVerticalAspectCorrection() => PeripageBitmapProtocol.VerticalAspectCorrection;

        /// <summary>
        /// Bind this to a UI Slider's onValueChanged for a live "dot density"
        /// panel. Updates the Inspector-visible field, pushes the value into
        /// PeripageBitmapProtocol.MaxAverageDensity immediately (so the very
        /// next preview/print reflects it), persists it via PlayerPrefs, and
        /// raises OnDensityChanged so any listening UI (e.g. a value label)
        /// can update without polling.
        /// </summary>
        public void SetMaxAverageDensity(float value)
        {
            PeripageBitmapProtocol.MaxAverageDensity = value; // clamps internally (0.05–0.9)
            maxAverageDensity = PeripageBitmapProtocol.MaxAverageDensity;
            PlayerPrefs.SetFloat(DensityPlayerPrefsKey, maxAverageDensity);
            OnDensityChanged?.Invoke(maxAverageDensity);
        }

        public float GetMaxAverageDensity() => PeripageBitmapProtocol.MaxAverageDensity;

        /// <summary>
        /// Convenience wrapper for a "Print calibration square" button on a
        /// debug panel: builds PeripageBitmapProtocol.BuildCalibrationSquareTexture()
        /// and sends it through the normal PrintPhoto path. Print this with
        /// verticalAspectCorrection at 1.0, measure the result with a ruler,
        /// compute the real correction, dial it in with the slider, and
        /// reprint to confirm.
        /// </summary>
        public void PrintCalibrationSquare(int squareSizePx = PeripageBitmapProtocol.PRINTER_WIDTH_PX)
        {
            Texture2D square = PeripageBitmapProtocol.BuildCalibrationSquareTexture(squareSizePx);
            PrintPhoto(square);
            Destroy(square);
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
    #if UNITY_ANDROID
            _bridge = new PeripageAndroidBridge(gameObject.name, this);
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
        /// <param name="source">The image to print.</param>
        /// <param name="rotate180">
        /// Pass true when the printer is mounted upside-down (e.g. built into
        /// a kiosk enclosure) so the physical output reads right-side up.
        /// Applied FIRST, before resize/margin — not after — so the blank cut
        /// margin (bottomCutMarginMm) still ends up physically last on the
        /// paper regardless of rotation. Rotating post-margin would instead
        /// put the blank margin at the top of the physical print and the
        /// image flush against the cut line, which is exactly backwards.
        /// </param>
        public void PrintPhoto(Texture2D source, bool rotate180 = false)
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

            Texture2D rotated = rotate180 ? PeripageBitmapProtocol.Rotate180(source) : source;

            // Resize to the printer's native width — via the same
            // PeripageBitmapProtocol.ResizeToWidth used by the preview path
            // (UIPeripagePrintDemo -> BuildDitheredPreview), so the print and
            // its preview always apply the identical resize + vertical dot-pitch
            // correction (PeripageBitmapProtocol.VerticalAspectCorrection) and
            // can't drift out of sync. The bridge/library still handles
            // monochrome thresholding/dithering internally on top of this.
            Texture2D resized = PeripageBitmapProtocol.ResizeToWidth(rotated, PeripageBitmapProtocol.PRINTER_WIDTH_PX);

            // Add the blank cut margin here, post-resize/pre-encode, so it
            // rides along as ordinary white pixel rows through whatever
            // bridge-side PNG-decode-and-pack happens next — no bridge needs
            // to know a margin exists.
            Texture2D withMargin = PeripageBitmapProtocol.AppendBottomMargin(resized, bottomCutMarginMm);
            byte[] pngBytes = withMargin.EncodeToPNG();

    #if UNITY_EDITOR
            if (saveMockPrintPreviewPng && _bridge is PeripageEditorMockBridge)
            {
                SavePreviewPng(withMargin);
            }
    #endif

            _bridge.PrintBitmap(pngBytes);

            if (withMargin != resized) Destroy(withMargin);
            if (resized != rotated) Destroy(resized);
            if (rotated != source) Destroy(rotated);
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