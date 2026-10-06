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
        #region Singleton

        public static PeripagePrinterManager Instance { get; private set; }

        #endregion

        #region Inspector

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

        #endregion

        #region Events

        /// <summary>Raised whenever the calibration value changes (Inspector, PlayerPrefs load, or SetVerticalAspectCorrection), so a debug UI can keep its slider/label in sync without polling.</summary>
        public event Action<float> OnCalibrationChanged;

        /// <summary>Raised whenever maxAverageDensity changes (Inspector, PlayerPrefs load, or SetMaxAverageDensity), so a debug UI can keep its slider/label in sync without polling.</summary>
        public event Action<float> OnDensityChanged;

        public event Action OnConnected;
        public event Action<string> OnConnectFailed;
        public event Action OnPrintComplete;
        public event Action<string> OnPrintFailed;

        #endregion

        #region Private Fields

        private IPeripageBridge _bridge;
        private bool _initialized;

        #endregion

        #region Unity Lifecycle

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

        #endregion

        #region Calibration

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

        #endregion

        #region Dot Density

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

        #endregion

        #region Bridge Initialization & Permissions

        /// <summary>
        /// Branches by actual Android OS version rather than assuming one
        /// permission model fits everything:
        ///   - API 31+ (Android 12+): BLUETOOTH_SCAN / BLUETOOTH_CONNECT are
        ///     the real runtime gate.
        ///   - API 23–30 (Android 6–11, includes older kiosk hardware):
        ///     BLUETOOTH / BLUETOOTH_ADMIN are "normal" permissions, granted
        ///     automatically at install — nothing to request for those.
        ///     ACCESS_COARSE_LOCATION is the actual runtime gate here.
        ///   - Below API 23: everything in the manifest is auto-granted at
        ///     install time; there's nothing to request at runtime at all.
        /// BLUETOOTH_SCAN/CONNECT literally don't exist as permission
        /// strings before API 31 — requesting them on an older device is a
        /// silent no-op with no dialog possible, which is why this branches
        /// instead of always requesting the API-31 pair.
        /// </summary>
        private void RequestBluetoothPermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            int sdk = GetAndroidSDKLevel();
            Debug.Log($"[PeripagePrinterManager] Android SDK_INT = {sdk}");

            if (sdk >= 31)
            {
                RequestAndInit(new[] { "android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT" });
            }
            else if (sdk >= 23)
            {
                RequestAndInit(new[] { "android.permission.ACCESS_COARSE_LOCATION" });
            }
            else
            {
                Debug.Log("[PeripagePrinterManager] SDK < 23 — permissions auto-granted at install.");
                InitBridge();
            }
            // On a kiosk, permissions can also just be granted once at setup time
            // via adb, so you don't have to handle the prompt UI at runtime:
            //   adb shell pm grant <package> android.permission.BLUETOOTH_CONNECT
            //   adb shell pm grant <package> android.permission.BLUETOOTH_SCAN
            //   adb shell pm grant <package> android.permission.ACCESS_COARSE_LOCATION
#else
            InitBridge();
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private int GetAndroidSDKLevel()
        {
            using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
            {
                return version.GetStatic<int>("SDK_INT");
            }
        }

        /// <summary>
        /// Requests a batch of permissions as a single native call (never two
        /// competing single-permission calls — that reliably suppresses both
        /// dialogs on many Android/Unity combinations), then calls
        /// InitBridge() exactly once, only after every permission in the
        /// batch has actually been answered (granted or denied either way).
        /// </summary>
        private void RequestAndInit(string[] permissions)
        {
            bool allGranted = true;
            foreach (var p in permissions)
            {
                bool granted = Permission.HasUserAuthorizedPermission(p);
                Debug.Log($"[PeripagePrinterManager] {p} granted = {granted}");
                allGranted &= granted;
            }

            if (allGranted)
            {
                InitBridge();
                return;
            }

            int remaining = permissions.Length;
            bool bridgeStarted = false;

            void TryInitOnce()
            {
                remaining--;
                if (remaining <= 0 && !bridgeStarted)
                {
                    bridgeStarted = true;
                    InitBridge();
                }
            }

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += p => { Debug.Log($"[PeripagePrinterManager] Granted: {p}"); TryInitOnce(); };
            callbacks.PermissionDenied += p => { Debug.LogWarning($"[PeripagePrinterManager] Denied: {p}"); TryInitOnce(); };
            callbacks.PermissionDeniedAndDontAskAgain += p => { Debug.LogWarning($"[PeripagePrinterManager] Permanently denied: {p}"); TryInitOnce(); };

            Permission.RequestUserPermissions(permissions, callbacks);
        }
#endif

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

        #endregion

        #region Connection

        public void Connect() => Connect(printerName);

        /// <summary>
        /// Connects to a specific address or name — used by
        /// PeripageAndroidDeviceDiscovery when the user picks a device from
        /// the paired list, bypassing the name-matching fallback in
        /// PeripageAndroidBridge.Connect() since the address is already exact.
        /// </summary>
        public void Connect(string addressOrName)
        {
            if (!_initialized) { Debug.LogWarning("Bridge not initialized yet"); return; }
            _bridge.Connect(addressOrName);
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
        /// Android-only: raw paired-device list ("Name|MAC" per entry), used
        /// by PeripageAndroidDeviceDiscovery to show a pick list. Empty on any
        /// other platform/bridge.
        /// </summary>
        public string[] GetPairedPrinters()
        {
            return _bridge is PeripageAndroidBridge androidBridge ? androidBridge.GetPairedPrinters() : Array.Empty<string>();
        }

        #endregion

        #region Printing

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

        #endregion

        #region Native Bluetooth Discovery (bypasses native .aar plugin)

        /// <summary>
        /// Reads already-paired ("bonded") devices directly via Android's own
        /// BluetoothAdapter, bypassing the native Peripage .aar plugin
        /// entirely. Added because that plugin's own getPairedPrinters()
        /// unconditionally checks for BLUETOOTH_CONNECT — an Android 12+-only
        /// permission that doesn't exist at all on pre-Android-12 hardware
        /// (e.g. this kiosk's API 25) — so it always throws a
        /// SecurityException there, even though the OS itself is happy to
        /// return the list once plain BLUETOOTH (a normal, auto-granted
        /// permission) is declared. Returns "Name|MAC" entries, the same
        /// format GetPairedPrinters() uses, so either can feed
        /// PeripageAndroidDeviceDiscovery.
        /// </summary>
        public string[] GetBondedDevicesNative()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
    try
    {
        using (var bluetoothAdapterClass = new AndroidJavaClass("android.bluetooth.BluetoothAdapter"))
        using (var adapter = bluetoothAdapterClass.CallStatic<AndroidJavaObject>("getDefaultAdapter"))
        {
            if (adapter == null)
            {
                Debug.LogWarning("[PeripagePrinterManager] No Bluetooth adapter on this device.");
                return Array.Empty<string>();
            }

            bool btEnabled = adapter.Call<bool>("isEnabled");
            int btState = adapter.Call<int>("getState"); // 12 = STATE_ON
            Debug.Log($"[PeripagePrinterManager] Bluetooth enabled = {btEnabled}, state = {btState} (12 = ON)");
            if (!btEnabled)
            {
                Debug.LogWarning("[PeripagePrinterManager] Bluetooth is OFF — getBondedDevices() returns nothing while it's off.");
                return Array.Empty<string>();
            }

            using (var bondedSet = adapter.Call<AndroidJavaObject>("getBondedDevices"))
            using (var iterator = bondedSet.Call<AndroidJavaObject>("iterator"))
            {
                var results = new System.Collections.Generic.List<string>();
                while (iterator.Call<bool>("hasNext"))
                {
                    using (var device = iterator.Call<AndroidJavaObject>("next"))
                    {
                        string name = device.Call<string>("getName");
                        string address = device.Call<string>("getAddress");
                        results.Add($"{name}|{address}");
                    }
                }
                Debug.Log($"[PeripagePrinterManager] GetBondedDevicesNative found {results.Count} device(s).");
                return results.ToArray();
            }
        }
    }
    catch (Exception e)
    {
        Debug.LogError($"[PeripagePrinterManager] GetBondedDevicesNative failed: {e.Message}");
        return Array.Empty<string>();
    }
#else
            return Array.Empty<string>();
#endif
        }

        /// <summary>
        /// Kicks off Android's native background Bluetooth discovery (for
        /// devices NOT already paired). On API 23–30 this requires
        /// IsLocationServiceActive() to be true, or it silently finds
        /// nothing. Results aren't returned synchronously — pair the device
        /// via Android's own Bluetooth settings once discovery reveals it,
        /// then GetBondedDevicesNative() will see it from then on.
        /// </summary>
        public bool StartNativeDiscovery()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!IsLocationServiceActive())
            {
                Debug.LogWarning("[PeripagePrinterManager] StartNativeDiscovery: location services are off — " +
                                  "Android won't return live scan results without it on this OS version.");
                return false;
            }

            try
            {
                using (var bluetoothAdapterClass = new AndroidJavaClass("android.bluetooth.BluetoothAdapter"))
                using (var adapter = bluetoothAdapterClass.CallStatic<AndroidJavaObject>("getDefaultAdapter"))
                {
                    if (adapter == null) return false;

                    bool isEnabled = adapter.Call<bool>("isEnabled");
                    if (!isEnabled)
                    {
                        Debug.LogWarning("[PeripagePrinterManager] Bluetooth radio is off — enabling...");
                        adapter.Call<bool>("enable");
                    }

                    bool started = adapter.Call<bool>("startDiscovery");
                    Debug.Log($"[PeripagePrinterManager] Native discovery started: {started}");
                    return started;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PeripagePrinterManager] StartNativeDiscovery failed: {e.Message}");
                return false;
            }
#else
            return false;
#endif
        }

        #endregion

        #region Location Service

        /// <summary>
        /// Checks whether at least one system location provider (GPS or
        /// network) is currently active. On Android 6–11, classic Bluetooth
        /// device discovery requires this to be on — it's not the same thing
        /// as the app having ACCESS_COARSE_LOCATION *permission* granted,
        /// both matter independently.
        /// Not Android, or the check fails for any reason, returns true so
        /// this never blocks the flow on platforms where it doesn't apply.
        /// </summary>
        public bool IsLocationServiceActive()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var locationService = currentActivity.Call<AndroidJavaObject>("getSystemService", "location"))
                {
                    bool gpsEnabled = locationService.Call<bool>("isProviderEnabled", "gps");
                    bool networkEnabled = locationService.Call<bool>("isProviderEnabled", "network");
                    Debug.Log($"[PeripagePrinterManager] Location providers — GPS: {gpsEnabled}, Network: {networkEnabled}");
                    return gpsEnabled || networkEnabled;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PeripagePrinterManager] Failed to check location providers: {e.Message}");
                return false;
            }
#else
            return true;
#endif
        }

        /// <summary>
        /// Opens Android's system Location Settings screen so the user (or a
        /// kiosk attendant) can enable a location provider without leaving the
        /// app's context entirely. No-ops with a warning on non-Android platforms.
        /// </summary>
        public void OpenLocationSettings()
        {
            Debug.Log("[PeripagePrinterManager] Opening Android System Location Settings...");
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = new AndroidJavaObject("android.content.Intent", "android.settings.LOCATION_SOURCE_SETTINGS"))
                {
                    intent.Call<AndroidJavaObject>("addFlags", 0x10000000); // FLAG_ACTIVITY_NEW_TASK
                    currentActivity.Call("startActivity", intent);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PeripagePrinterManager] Failed to open location settings: {e.Message}");
            }
#else
            Debug.LogWarning("[PeripagePrinterManager] Not on Android — cannot open location settings.");
#endif
        }

        #endregion

        #region Native Callbacks

        // Callbacks invoked by Kotlin via UnitySendMessage.
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

        public void OnNativeLogCallback(string message)
        {
            Debug.Log($"[Native] {message}");
        }
        #endregion
    }
}