using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TechArt.Module.Peripage;

namespace TechArt.Module.Peripage.Demo
{
    /// <summary>
    /// Implements the requested print-zone flow:
    ///   1. printZone (RectTransform) marks the screen area to crop
    ///   2. croppedPreviewImage shows that raw cropped capture
    ///   3. printerQualityPreviewImage shows a simulated printer-resolution
    ///      version (384px wide, thresholded black/white) so the user can see
    ///      roughly what the thermal print will actually look like
    ///   4. Print button sends the real (non-downsampled) crop through the
    ///      existing PeripagePrinterManager, which already handles resizing
    ///      and calling into whichever bridge (Android/Mac/mock) is active
    ///
    /// Attach to any GameObject in your Canvas and wire up the fields in the
    /// Inspector. Uses UnityEngine.UI (RawImage, Button) — swap for TMP/UI
    /// Toolkit equivalents if your kiosk UI uses those instead.
    /// </summary>
    public class PeripagePrintDemo : MonoBehaviour
    {
        [Header("Print zone")]
        [Tooltip("The RectTransform whose on-screen area gets captured and printed.")]
        public RectTransform printZone;

        [Tooltip("Camera used to render printZone's contents, if it's 3D/world content " +
                 "behind the UI (e.g. a live photo booth scene). Leave null if printZone " +
                 "only contains ordinary UI (Screen Space - Overlay canvas).")]
        public Camera captureCamera;

        [Header("Previews")]
        public RawImage croppedPreviewImage;
        public RawImage printerQualityPreviewImage;

        [Header("Capture")]
        [Tooltip("Button that triggers a one-shot capture + preview refresh " +
                 "(use this instead of/alongside Live Preview).")]
        public Button captureButton;

        [Header("Print")]
        public Button printButton;
        public PeripagePrinterManager printerManager;

        [Header("Behaviour")]
        [Tooltip("Automatically refresh both previews every frame. Turn off and use " +
                 "the Capture button (or call RefreshPreview() manually) instead if " +
                 "that's too expensive for your scene.")]
        public bool livePreview = false;

        private Texture2D _lastCapture;

        void Start()
        {
            if (captureButton != null) captureButton.onClick.AddListener(OnCapturePressed);
            if (printButton != null) printButton.onClick.AddListener(OnPrintPressed);
            if (printerManager == null) printerManager = FindObjectOfType<PeripagePrinterManager>();
        }

        void Update()
        {
            // Note: unlike the button-triggered path, calling ReadPixels directly
            // from Update (rather than via WaitForEndOfFrame) works here because
            // Update runs before rendering for the CURRENT frame but after the
            // PREVIOUS frame finished drawing — so this reads last frame's fully
            // rendered buffer, which is fine for a continuous live preview. It's
            // a one-frame-stale read, imperceptible at normal frame rates.
            if (livePreview) RefreshPreview();
        }

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

        private IEnumerator RefreshPreviewNextFrame()
        {
            yield return new WaitForEndOfFrame();
            RefreshPreview();
        }

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
            if (screenRect.width < 1 || screenRect.height < 1) return;

            Texture2D capture = CaptureScreenRegion(screenRect);
            if (capture == null) return;

            if (_lastCapture != null) Destroy(_lastCapture);
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
                    Destroy(printerQualityPreviewImage.texture);
                printerQualityPreviewImage.texture = printerPreview;
            }
        }

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
        /// Simulates what the thermal printer will actually produce: resized to
        /// the printer's native 384px width, converted to grayscale, and
        /// thresholded to pure black/white (thermal heads have no grayscale —
        /// it's on or off per dot). This is preview-only; the actual bytes sent
        /// to print go through PeripagePrinterManager, which may apply proper
        /// dithering via the printer library itself rather than this flat
        /// threshold.
        /// </summary>
        private Texture2D BuildPrinterQualityPreview(Texture2D source)
        {
            const int printerWidth = PeripagePrinterManager.PRINTER_WIDTH_PX;
            int printerHeight = Mathf.RoundToInt(source.height * (printerWidth / (float)source.width));

            RenderTexture rt = RenderTexture.GetTemporary(printerWidth, printerHeight);
            Graphics.Blit(source, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D resized = new Texture2D(printerWidth, printerHeight, TextureFormat.RGB24, false);
            resized.ReadPixels(new Rect(0, 0, printerWidth, printerHeight), 0, 0);
            resized.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            // Threshold to black/white so the preview actually reflects what a
            // thermal head can produce (no grayscale dots).
            Color32[] pixels = resized.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                float luminance = (0.299f * pixels[i].r + 0.587f * pixels[i].g + 0.114f * pixels[i].b) / 255f;
                byte value = luminance < 0.5f ? (byte)0 : (byte)255;
                pixels[i] = new Color32(value, value, value, 255);
            }
            resized.SetPixels32(pixels);
            resized.Apply();

            return resized;
        }

        /// <summary>
        /// Print button handler: captures a fresh, full-quality (non-thresholded)
        /// crop and hands it to PeripagePrinterManager, which already knows how
        /// to resize/encode/send it through whichever bridge is active.
        /// </summary>
        public void OnPrintPressed()
        {
            StartCoroutine(CaptureAndPrintNextFrame());
        }

        private IEnumerator CaptureAndPrintNextFrame()
        {
            // Wait for end of frame so any UI just interacted with (e.g. the
            // Print button's own pressed-state visuals) doesn't get baked into
            // the capture.
            yield return new WaitForEndOfFrame();

            Rect screenRect = GetScreenRectForPrintZone();
            Texture2D capture = CaptureScreenRegion(screenRect);

            if (printerManager == null)
            {
                Debug.LogWarning("[PeripagePrintZoneCapture] No PeripagePrinterManager assigned/found — cannot print.");
                Destroy(capture);
                yield break;
            }

            printerManager.PrintPhoto(capture);
            Destroy(capture); // PrintPhoto already copies what it needs (resizes internally)
        }

        void OnDestroy()
        {
            if (_lastCapture != null) Destroy(_lastCapture);
        }
    }
}
