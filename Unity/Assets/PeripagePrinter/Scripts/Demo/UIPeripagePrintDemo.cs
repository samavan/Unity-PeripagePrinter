using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TechArt.Module.Peripage;
using TMPro;

namespace TechArt.Module.Peripage.Demo
{
    /// <summary>
    /// Implements the print-zone flow:
    ///   1. printZone (RectTransform) marks the screen area to crop
    ///   2. croppedPreviewImage shows that raw cropped capture
    ///   3. printerQualityPreviewImage shows the actual black/white dithered
    ///      print result (384px wide — via PeripageBitmapProtocol.
    ///      BuildDitheredPreview) so the user can see exactly what the
    ///      thermal print will look like, dot pattern and all
    ///   4. Print button sends the real (non-downsampled) crop through the
    ///      existing PeripagePrinterManager, which already handles resizing
    ///      and calling into whichever bridge (Android/Mac/mock) is active
    ///
    /// Attach to any GameObject in your Canvas and wire up the fields in the
    /// Inspector.
    /// </summary>
    public class UIPeripagePrintDemo : MonoBehaviour
    {
        #region Inspector

        [Header("Print zone")]
        [Tooltip("The RectTransform whose on-screen area gets captured and printed.")]
        [SerializeField] private RectTransform printZone;

        [Tooltip("Camera used to render printZone's contents, if it's 3D/world content " +
                 "behind the UI (e.g. a live photo booth scene). Leave null if printZone " +
                 "only contains ordinary UI (Screen Space - Overlay canvas).")]
        [SerializeField] private Camera captureCamera;

        [Header("Previews")]
        [SerializeField] private RawImage croppedPreviewImage;
        [SerializeField] private RawImage printerQualityPreviewImage;

        [Header("Capture")]
        [Tooltip("Button that triggers a one-shot capture + preview refresh " +
                 "(use this instead of/alongside Live Preview).")]
        [SerializeField] private Button captureButton;

        [Header("Print")]
        [Tooltip("Prints the last capture, rotated per rotationDropdown's selection.")]
        [SerializeField] private Button printButton;
        [SerializeField] private PeripagePrinterManager printerManager;

        [Header("Rotation")]
        [Tooltip("Selects the orientation Print sends to the printer. \"Original\" (index 0) " +
                 "prints as captured. \"180°\" (index 1) rotates the last capture 180° first — " +
                 "for a printer mounted upside-down (e.g. built into a kiosk enclosure) so the " +
                 "physical output reads right-side up. If left with no options configured in the " +
                 "Inspector, Start() populates it with these two entries automatically.")]
        [SerializeField] private TMP_Dropdown rotationDropdown;

        [Header("Behaviour")]
        [Tooltip("Automatically refresh both previews every frame. Turn off and use " +
                 "the Capture button (or call RefreshPreview() manually) instead if " +
                 "that's too expensive for your scene.")]
        [SerializeField] private bool livePreview = false;

        [Header("Calibration (debug)")]
        [Tooltip("Optional. Drives PeripagePrinterManager.SetVerticalAspectCorrection " +
                 "live — drag it and the printer-quality preview redraws immediately, " +
                 "so you can dial in the vertical dot-pitch correction by eye before " +
                 "committing it to an actual print. Slider's min/max are read from " +
                 "PeripageBitmapProtocol's calibration clamp (0.1–5.0) on Start; set " +
                 "narrower min/max in the Inspector if you want a finer-grained drag " +
                 "range (e.g. 0.5–2.0, which covers every printer this has been seen on).")]
        [SerializeField] private Slider calibrationSlider;

        [Tooltip("Optional. Shows the current correction value as text (e.g. \"1.18\"). " +
                 "Works with either UnityEngine.UI.Text or TextMeshProUGUI — wire " +
                 "whichever you're using by assigning its GameObject and this script " +
                 "will look for either component.")]
        [SerializeField] private GameObject calibrationValueLabel;

        [Tooltip("Optional. Prints PeripageBitmapProtocol.BuildCalibrationSquareTexture() " +
                 "through printerManager — the reference square you measure with a " +
                 "ruler to derive the correction value in the first place.")]
        [SerializeField] private Button printCalibrationSquareButton;

        [Header("Dot density (debug)")]
        [Tooltip("Optional. Drives PeripagePrinterManager.SetMaxAverageDensity live — " +
                 "drag it and the printer-quality preview redraws immediately, so you " +
                 "can dial in how much ink/how many dots print by eye. Lower = cleaner/" +
                 "lighter prints, higher = darker/more midtone detail. Slider's min/max " +
                 "are set to PeripageBitmapProtocol's clamp range (0.05–0.9) on Start.")]
        [SerializeField] private Slider densitySlider;

        [Tooltip("Optional. Shows the current density cap as text (e.g. \"0.42\"). " +
                 "Works with either UnityEngine.UI.Text or TextMeshProUGUI.")]
        [SerializeField] private GameObject densityValueLabel;

        #endregion

        #region Private Fields

        private Texture2D _lastCapture;

        #endregion

        #region Unity Lifecycle

        private void Start()
        {
            if (captureButton != null)
            {
                captureButton.onClick.AddListener(OnCapturePressed);
            }

            if (printButton != null)
            {
                printButton.onClick.AddListener(OnPrintPressed);
            }

            if (rotationDropdown != null)
            {
                rotationDropdown.ClearOptions();
                rotationDropdown.AddOptions(new System.Collections.Generic.List<string> { "Original", "180°" });
                rotationDropdown.value = 0;
                rotationDropdown.RefreshShownValue();
            }

            if (printerManager == null)
            {
                printerManager = FindObjectOfType<PeripagePrinterManager>();
            }

            SetUpCalibrationControls();
        }

        /// <summary>
        /// Wires the optional calibration slider/label/button to
        /// PeripagePrinterManager's live VerticalAspectCorrection. All three
        /// fields are optional — skips whatever isn't assigned, so this demo
        /// still works fine without a calibration panel in the scene.
        /// </summary>
        private void SetUpCalibrationControls()
        {
            if (printerManager == null) return;

            float current = printerManager.GetVerticalAspectCorrection();

            if (calibrationSlider != null)
            {
                // Only overwrite min/max if left at the Slider's own default
                // (0..1), so an Inspector-set range isn't clobbered.
                if (Mathf.Approximately(calibrationSlider.minValue, 0f) && Mathf.Approximately(calibrationSlider.maxValue, 1f))
                {
                    calibrationSlider.minValue = 0.5f;
                    calibrationSlider.maxValue = 2f;
                }

                calibrationSlider.SetValueWithoutNotify(current);
                calibrationSlider.onValueChanged.AddListener(OnCalibrationSliderChanged);
            }

            if (printCalibrationSquareButton != null)
            {
                printCalibrationSquareButton.onClick.AddListener(() => printerManager.PrintCalibrationSquare());
            }

            printerManager.OnCalibrationChanged += OnCalibrationChangedExternally;
            UpdateCalibrationLabel(current);

            float currentDensity = printerManager.GetMaxAverageDensity();

            if (densitySlider != null)
            {
                if (Mathf.Approximately(densitySlider.minValue, 0f) && Mathf.Approximately(densitySlider.maxValue, 1f))
                {
                    densitySlider.minValue = 0.05f;
                    densitySlider.maxValue = 0.9f;
                }

                densitySlider.SetValueWithoutNotify(currentDensity);
                densitySlider.onValueChanged.AddListener(OnDensitySliderChanged);
            }

            printerManager.OnDensityChanged += OnDensityChangedExternally;
            UpdateDensityLabel(currentDensity);
        }

        /// <summary>
        /// Slider drag handler: pushes the new value into the printer manager
        /// (which persists it and updates PeripageBitmapProtocol's live value),
        /// then immediately refreshes the printer-quality preview so dragging
        /// the slider gives instant visual feedback without printing anything.
        /// </summary>
        private void OnCalibrationSliderChanged(float value)
        {
            printerManager.SetVerticalAspectCorrection(value);
            UpdateCalibrationLabel(printerManager.GetVerticalAspectCorrection());

            if (_lastCapture != null)
            {
                RefreshPreviewFromLastCapture();
            }
        }

        /// <summary>
        /// Keeps the slider/label in sync if the correction changes from
        /// somewhere other than this slider (e.g. loaded from PlayerPrefs on
        /// Awake, or another calibration UI instance).
        /// </summary>
        private void OnCalibrationChangedExternally(float value)
        {
            if (calibrationSlider != null)
            {
                calibrationSlider.SetValueWithoutNotify(value);
            }
            UpdateCalibrationLabel(value);
        }

        private void UpdateCalibrationLabel(float value)
        {
            SetLabelText(calibrationValueLabel, value.ToString("0.00"));
        }

        /// <summary>
        /// Slider drag handler for the density cap: pushes the new value into
        /// the printer manager, then immediately refreshes the printer-quality
        /// preview so dragging gives instant visual feedback.
        /// </summary>
        private void OnDensitySliderChanged(float value)
        {
            printerManager.SetMaxAverageDensity(value);
            UpdateDensityLabel(printerManager.GetMaxAverageDensity());

            if (_lastCapture != null)
            {
                RefreshPreviewFromLastCapture();
            }
        }

        /// <summary>
        /// Keeps the density slider/label in sync if the value changes from
        /// somewhere other than this slider (e.g. loaded from PlayerPrefs on
        /// Awake, or another calibration UI instance).
        /// </summary>
        private void OnDensityChangedExternally(float value)
        {
            if (densitySlider != null)
            {
                densitySlider.SetValueWithoutNotify(value);
            }
            UpdateDensityLabel(value);
        }

        private void UpdateDensityLabel(float value)
        {
            SetLabelText(densityValueLabel, value.ToString("0.00"));
        }

        /// <summary>
        /// Sets text on either a legacy UnityEngine.UI.Text or a TextMeshPro
        /// label, whichever is present, WITHOUT needing a "TMP_PRESENT"
        /// scripting define symbol to be set up in Player Settings. The
        /// previous version gated the TMP branch behind #if TMP_PRESENT — a
        /// project-specific symbol that has to be added manually under
        /// Project Settings > Player > Scripting Define Symbols, and is easy
        /// to forget. If it's never added, that whole branch is compiled out,
        /// so a TMP label silently never updates (the fallback GetComponent
        /// &lt;Text&gt; finds nothing on a TMP object and does nothing) — which
        /// matches "the label doesn't update" with no error anywhere. Using
        /// reflection instead removes the dependency on that define entirely;
        /// it works whether or not the TextMeshPro package is installed, and
        /// whether or not anyone remembered to set the symbol.
        /// If your label still doesn't update after this fix, double check
        /// calibrationValueLabel/densityValueLabel are actually assigned in
        /// the Inspector — a null reference here silently no-ops too.
        /// </summary>
        private static readonly System.Type TmpTextType =
            System.Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");

        private void SetLabelText(GameObject label, string text)
        {
            if (label == null) return;

            if (TmpTextType != null)
            {
                var tmpComponent = label.GetComponent(TmpTextType);
                if (tmpComponent != null)
                {
                    TmpTextType.GetProperty("text")?.SetValue(tmpComponent, text);
                    return;
                }
            }

            var uiText = label.GetComponent<Text>();
            if (uiText != null)
            {
                uiText.text = text;
            }
        }

        /// <summary>
        /// Re-runs the dithered preview against the last capture already on
        /// screen (rather than the full RefreshPreview, which would also
        /// re-capture the screen region) — cheaper for "just re-dither with
        /// the new calibration" while dragging the slider.
        /// </summary>
        private void RefreshPreviewFromLastCapture()
        {
            if (_lastCapture == null || printerQualityPreviewImage == null) return;

            Texture2D printerPreview = BuildPrinterQualityPreview(_lastCapture);
            if (printerQualityPreviewImage.texture != null)
            {
                Destroy(printerQualityPreviewImage.texture);
            }
            printerQualityPreviewImage.texture = printerPreview;
        }

        private void Update()
        {
            // Note: unlike the button-triggered path, calling ReadPixels directly
            // from Update (rather than via WaitForEndOfFrame) works here because
            // Update runs before rendering for the CURRENT frame but after the
            // PREVIOUS frame finished drawing — so this reads last frame's fully
            // rendered buffer, which is fine for a continuous live preview. It's
            // a one-frame-stale read, imperceptible at normal frame rates.
            if (livePreview)
            {
                RefreshPreview();
            }
        }

        private void OnDestroy()
        {
            if (_lastCapture != null)
            {
                Destroy(_lastCapture);
            }

            if (printerManager != null)
            {
                printerManager.OnCalibrationChanged -= OnCalibrationChangedExternally;
                printerManager.OnDensityChanged -= OnDensityChangedExternally;
            }
        }

        #endregion

        #region Button Handlers

        /// <summary>
        /// Capture button handler — routes through a coroutine so ReadPixels runs
        /// after the current frame has finished rendering (calling it directly
        /// from a Button.onClick handler mid-Update throws "not inside drawing
        /// frame", since a UI click callback fires before that frame is drawn).
        /// </summary>
        public void OnCapturePressed()
        {
            StartCoroutine(RefreshPreviewNextFrame());
        }

        /// <summary>
        /// Print button handler: sends whatever's currently in _lastCapture
        /// (i.e. exactly what the user is looking at in the preview) to
        /// PeripagePrinterManager, rotated 180° first if rotationDropdown is
        /// set to that option.
        /// </summary>
        public void OnPrintPressed()
        {
            PrintLastCapture(rotate180: IsRotation180Selected());
        }

        /// <summary>
        /// Index 1 ("180°") in rotationDropdown means rotate; index 0
        /// ("Original") or no dropdown assigned means print as captured.
        /// </summary>
        private bool IsRotation180Selected()
        {
            return rotationDropdown != null && rotationDropdown.value == 1;
        }

        /// <summary>
        /// Shared print logic: sends _lastCapture (i.e. exactly what the user
        /// is looking at in the preview) to PeripagePrinterManager, which
        /// already knows how to resize/encode/send it through whichever
        /// bridge is active. Deliberately does NOT re-capture the screen —
        /// printing must reproduce the previewed image, not whatever
        /// printZone happens to contain at click time. Use the Capture
        /// button (or RefreshPreview()) first to populate _lastCapture.
        /// </summary>
        private void PrintLastCapture(bool rotate180)
        {
            if (_lastCapture == null)
            {
                Debug.LogWarning("[UIPeripagePrintDemo] No capture available to print — press Capture first.");
                return;
            }

            if (printerManager == null)
            {
                Debug.LogWarning("[UIPeripagePrintDemo] No PeripagePrinterManager assigned/found — cannot print.");
                return;
            }

            printerManager.PrintPhoto(_lastCapture, rotate180);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Captures the current screen content under printZone and updates both
        /// preview images. Safe to call manually (e.g. from a "Refresh" button)
        /// if livePreview is off — but if calling this directly from a UI event
        /// handler (button click), prefer OnCapturePressed() instead, which wraps
        /// this in the WaitForEndOfFrame timing ReadPixels requires.
        /// </summary>
        public void RefreshPreview()
        {
            Rect screenRect = GetScreenRectForPrintZone();
            if (screenRect.width < 1 || screenRect.height < 1)
            {
                return;
            }

            Texture2D capture = CaptureScreenRegion(screenRect);
            if (capture == null)
            {
                return;
            }

            if (_lastCapture != null)
            {
                Destroy(_lastCapture);
            }

            _lastCapture = capture;

            if (croppedPreviewImage != null)
            {
                croppedPreviewImage.texture = capture;
            }

            if (printerQualityPreviewImage != null)
            {
                Texture2D printerPreview = BuildPrinterQualityPreview(capture);
                // RawImage just needs a texture reference; destroy the previous
                // one to avoid leaking a new texture every frame under livePreview.
                if (printerQualityPreviewImage.texture != null)
                {
                    Destroy(printerQualityPreviewImage.texture);
                }

                printerQualityPreviewImage.texture = printerPreview;
            }
        }

        #endregion

        #region Coroutines

        private IEnumerator RefreshPreviewNextFrame()
        {
            yield return new WaitForEndOfFrame();
            RefreshPreview();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Converts printZone's RectTransform corners into screen-space pixel
        /// coordinates, handling both Screen Space - Overlay (camera-less) and
        /// Screen Space - Camera / World Space canvases.
        /// </summary>
        private Rect GetScreenRectForPrintZone()
        {
            Vector3[] corners = new Vector3[4];
            printZone.GetWorldCorners(corners); // bottom-left, top-left, top-right, bottom-right

            Camera cam = captureCamera; // null is valid for Overlay canvases
            Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

            float x = Mathf.Min(min.x, max.x);
            float y = Mathf.Min(min.y, max.y);
            float width = Mathf.Abs(max.x - min.x);
            float height = Mathf.Abs(max.y - min.y);

            return new Rect(x, y, width, height);
        }

        private Texture2D CaptureScreenRegion(Rect screenRect)
        {
            // Screen.height - y flips from UI's bottom-left-origin screen space
            // into ReadPixels' bottom-left-origin texture space correctly — both
            // are actually bottom-left already in Unity, so no flip needed here;
            // kept explicit/clamped for safety against off-screen rects.
            int x = Mathf.Clamp(Mathf.RoundToInt(screenRect.x), 0, Screen.width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(screenRect.y), 0, Screen.height - 1);
            int width = Mathf.Clamp(Mathf.RoundToInt(screenRect.width), 1, Screen.width - x);
            int height = Mathf.Clamp(Mathf.RoundToInt(screenRect.height), 1, Screen.height - y);

            // Must be called at end of frame for ReadPixels to see fully-rendered
            // content. For a Print button click this is fine as-is; for the
            // livePreview-every-frame path this is a known perf cost — turn
            // livePreview off for production kiosk builds and refresh on-demand
            // instead (e.g. after the user finishes positioning their photo).
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(x, y, width, height), 0, 0);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Shows exactly what will be printed: same resize, same density cap,
        /// same dithering as the real print path. All actual image-conversion
        /// math lives in PeripageBitmapProtocol — the same class that builds the
        /// real packed print data — via BuildDitheredPreview, which shares its
        /// luminance/dithering code with BuildPackedBitmap. So this preview
        /// can't silently drift out of sync with what actually prints, and it
        /// reflects the real dot pattern (not a smooth grayscale approximation)
        /// so density-cap and dithering changes are visible before committing
        /// paper. This method is just a thin call-through; don't reimplement
        /// the conversion here.
        /// </summary>
        private Texture2D BuildPrinterQualityPreview(Texture2D source)
        {
            return PeripageBitmapProtocol.BuildDitheredPreview(source, PeripageBitmapProtocol.PRINTER_WIDTH_PX);
        }

        #endregion
    }
}