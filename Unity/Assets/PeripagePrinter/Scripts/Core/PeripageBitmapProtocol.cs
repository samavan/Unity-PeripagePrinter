using UnityEngine;

namespace TechArt.Module.Peripage
{
    /// <summary>
    /// Platform-agnostic encoder for the Peripage A6/A6+ bitmap print protocol.
    /// Pure C# / Unity API only — no platform bridge code, no P/Invoke — so any
    /// bridge (PeripageMacBridge today; a future iOS bridge, etc.) can reuse it
    /// instead of re-implementing the same PNG-decode-and-pack logic.
    ///
    /// This exists because the Peripage does NOT understand PNG, ESC/POS, or any
    /// standard printer language — it needs a raw packed 1bpp bitmap wrapped in
    /// its own proprietary framing. Whichever platform's native layer is just a
    /// raw byte pipe (no image-aware library underneath, unlike the Android
    /// Kotlin lib which apparently does this conversion internally) needs this.
    ///
    /// Protocol reconstructed from public reverse-engineering of captured
    /// Bluetooth traffic between the printer and the official app (see
    /// eliasweingaertner/peripage-A6-bluetooth and bitrate16/peripage-python on
    /// GitHub), cross-checked against bitbank2/Thermal_Printer's independent
    /// PeriPage support (same magic bytes: 10 FF FE 01 prefix, 1B 4A 40 10 FF FE 45
    /// footer). Not official documentation — Peripage has never published one —
    /// so treat it as "known to work for others", not guaranteed.
    ///
    /// Packet shape for one printed image:
    ///   1) 10 FF FE 01                shared PRINT_PREFIX  — "print start"
    ///   2) 12 zero bytes              shared PRINT_PADDING — padding/sync, purpose undocumented
    ///   3) 1D 76 30 00 wL wH hL hH    BuildRasterHeader()  — width-in-BYTES (LE) + height-in-PIXELS (LE)
    ///   4) raw packed 1bpp image data BuildPackedBitmap()  — MSB-first, bit=1 = "heat/print this dot"
    ///   5) 1B 4A 40 10 FF FE 45       shared PRINT_FOOTER  — "end of print"
    ///
    /// The image data (step 4) should be sent to the printer in whole-row
    /// chunks (GetRowAlignedChunkSize) with a short delay between them
    /// (RECOMMENDED_CHUNK_DELAY) — sending it all in one call risks overrunning
    /// the printer's Bluetooth receive buffer, and a chunk boundary that
    /// splits a row corrupts the row framing on transports that write each
    /// chunk as a discrete packet (e.g. BLE GATT). Chunking/timing is left to
    /// each bridge since it depends on how that platform's transport is
    /// invoked (e.g. from a coroutine on Mac).
    /// </summary>
    public static class PeripageBitmapProtocol
    {
        public const int PRINTER_WIDTH_PX = 384;

        public const float RECOMMENDED_CHUNK_DELAY = 0.02f; // 20ms between chunks

        public const float DEFAULT_VERTICAL_ASPECT_CORRECTION = 1.0f;

        private static float _verticalAspectCorrection = DEFAULT_VERTICAL_ASPECT_CORRECTION;

        /// <summary>
        /// Corrects for the printer's non-square dot pitch. PRINTER_WIDTH_PX maps
        /// to a fixed physical paper width (horizontal dot pitch), but the paper
        /// advance per printed row (vertical pitch) is a separate physical
        /// measurement set by the stepper motor/firmware — nothing says the two
        /// have to match. Left at 1.0 (i.e. resizing that preserves pixel aspect
        /// ratio 1:1), a print comes out vertically squeezed whenever the actual
        /// vertical pitch is finer than the horizontal one, because the same
        /// pixel-square image maps to fewer physical mm of paper per row than
        /// per column.
        ///
        /// This is NOT part of the reverse-engineered protocol — nothing in the
        /// wire format states it — so it must be measured for this printer/
        /// firmware, not assumed. Used to be a `const`, which meant calibrating
        /// it required editing code and recompiling; it's a mutable, clamped
        /// (0.1–5.0) static property now so a debug-UI slider (see
        /// UIPeripagePrintDemo) can drive it directly and you can watch the
        /// printer-quality preview update live while you drag it. To calibrate,
        /// use BuildCalibrationSquareTexture and
        /// ComputeVerticalAspectCorrectionFromMeasurement below:
        ///   1. Print BuildCalibrationSquareTexture() with this left at 1.0.
        ///   2. Measure the printed square's actual width and height in mm with a ruler.
        ///   3. correction = ComputeVerticalAspectCorrectionFromMeasurement(measuredWidthMm, measuredHeightMm).
        ///   4. Set this to that value (slider, or PeripagePrinterManager's Inspector
        ///      field), re-print the square, and re-measure once to confirm it now
        ///      comes out square (equal width and height).
        ///
        /// NOTE: an earlier version of this doc comment had step 3 inverted
        /// (measuredHeight / measuredWidth). That's backwards: printing an N x N
        /// pixel square at factor 1.0 produces measuredWidthMm = N * dx and
        /// measuredHeightMm = N * dy, where dx/dy are the printer's fixed
        /// horizontal/vertical mm-per-dot pitch. The correction that stretches
        /// the image back to square is dx/dy = measuredWidthMm / measuredHeightMm
        /// — width over height, not the other way around. Using the inverted
        /// formula produces a correction on the wrong side of 1.0 (e.g. 0.8
        /// instead of 1.25), which shrinks the image vertically instead of
        /// stretching it — worsening the squeeze instead of fixing it, and
        /// also being the likely cause of the "dots merging into the same row"
        /// look: with too little vertical stretch, adjacent print rows land
        /// closer together than the thermal head's dot pitch actually needs,
        /// so consecutive rows visually overlap instead of reading as evenly
        /// spaced. A value > 1.0 stretches the image taller before dithering,
        /// to counteract vertical squeeze on the physical print.
        /// </summary>
        public static float VerticalAspectCorrection
        {
            get => _verticalAspectCorrection;
            set => _verticalAspectCorrection = Mathf.Clamp(value, 0.1f, 5.0f);
        }

        /// <summary>
        /// Nominal printable paper width in mm for a 384px/203dpi direct-thermal
        /// panel (384px / 8 dots-per-mm = 48mm). Used ONLY to convert
        /// millimeter inputs (calibration squares, cut margins) into dot rows —
        /// it does not affect the fixed PRINTER_WIDTH_PX resize target. If your
        /// paper stock's printable area measures differently, update this.
        /// </summary>
        public const float PRINTER_PAPER_WIDTH_MM = 48f;

        /// <summary>
        /// Converts a physical length in millimeters to a whole number of print
        /// rows (dots), using the printer's fixed horizontal dot pitch
        /// (PRINTER_WIDTH_PX / PRINTER_PAPER_WIDTH_MM) scaled by
        /// verticalAspectCorrection to get the vertical dot pitch. Defaults to
        /// the current live VerticalAspectCorrection so it always reflects
        /// whatever the slider/Inspector is currently set to unless a caller
        /// explicitly overrides it.
        /// </summary>
        public static int MillimetersToDotRows(float mm, float? verticalAspectCorrection = null)
        {
            float correction = verticalAspectCorrection ?? VerticalAspectCorrection;
            float horizontalDotsPerMm = PRINTER_WIDTH_PX / PRINTER_PAPER_WIDTH_MM;
            float verticalDotsPerMm = horizontalDotsPerMm * correction;
            return Mathf.Max(0, Mathf.RoundToInt(mm * verticalDotsPerMm));
        }

        /// <summary>
        /// Builds a solid black square, sizePx x sizePx, for calibrating
        /// VerticalAspectCorrection. Print this through the normal
        /// PrintPhoto path with the correction temporarily left at 1.0, measure
        /// the physical result with a ruler, and feed the two measurements into
        /// ComputeVerticalAspectCorrectionFromMeasurement. Caller owns (and
        /// should Destroy) the returned texture.
        /// </summary>
        public static Texture2D BuildCalibrationSquareTexture(int sizePx = PRINTER_WIDTH_PX)
        {
            var tex = new Texture2D(sizePx, sizePx, TextureFormat.RGB24, false);
            var black = new Color32(0, 0, 0, 255);
            var pixels = new Color32[sizePx * sizePx];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = black;
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Given the actual measured width/height (in mm, ruler on the printed
        /// calibration square) of a square printed with correction = 1.0,
        /// returns the value to set VerticalAspectCorrection to so it prints
        /// square. See VerticalAspectCorrection's doc comment for the
        /// derivation (it's measuredWidth / measuredHeight, not the other way
        /// around).
        /// </summary>
        public static float ComputeVerticalAspectCorrectionFromMeasurement(float measuredPrintedWidthMm, float measuredPrintedHeightMm)
        {
            if (measuredPrintedHeightMm <= 0f)
            {
                Debug.LogWarning("[PeripageBitmapProtocol] measuredPrintedHeightMm must be > 0 — returning 1.0 (no correction).");
                return 1f;
            }
            return measuredPrintedWidthMm / measuredPrintedHeightMm;
        }

        /// <summary>
        /// Returns a chunk size (in bytes) that is a whole multiple of
        /// bytesPerRow — i.e. every write starts and ends on a row boundary,
        /// so a dropped/misordered/misaligned write can only ever corrupt
        /// whole rows, never desync the row framing itself.
        ///
        /// Defaults to exactly ONE row per chunk. This isn't a guess: it's
        /// the exact chunking used by linglingltd/peripage-a6-control, whose
        /// protocol constants (10 FF FE 01 prefix, 12-byte zero pad,
        /// 1B 4A 40 10 FF FE 45 footer) are a byte-for-byte match to this
        /// class's — confirming it's talking to the same 384px/203dpi A6
        /// hardware this protocol targets. That reference sends
        /// `chunksize = 48  # a chunk is one line, 48byte * 8 = 384bit`
        /// with a 20ms delay between each row-sized write. An earlier
        /// version of this helper picked as many whole rows as fit under a
        /// borrowed 122-byte figure from a different (RFCOMM, not
        /// necessarily row-chunked) reference; that number had no connection
        /// to this hardware and is dropped in favour of matching the
        /// validated reference exactly.
        ///
        /// rowsPerChunk can be raised (e.g. for a faster transport that
        /// doesn't need per-row pacing) but should stay a whole-row multiple.
        /// </summary>
        public static int GetRowAlignedChunkSize(int bytesPerRow, int rowsPerChunk = 1)
        {
            if (bytesPerRow <= 0) return bytesPerRow;
            if (rowsPerChunk < 1) rowsPerChunk = 1;

            return rowsPerChunk * bytesPerRow;
        }

        public static readonly byte[] PRINT_PREFIX = { 0x10, 0xFF, 0xFE, 0x01 };
        public static readonly byte[] PRINT_PADDING = new byte[12]; // 12 zero bytes
        public static readonly byte[] PRINT_FOOTER = { 0x1B, 0x4A, 0x40, 0x10, 0xFF, 0xFE, 0x45 };

        /// <summary>
        /// Decodes PNG bytes (e.g. from Texture2D.EncodeToPNG) into a packed 1bpp
        /// bitmap ready to send to the printer. Returns false if the PNG couldn't
        /// be decoded.
        /// </summary>
        public static bool TryConvertPngToPackedBitmap(byte[] pngBytes, out byte[] packedBitmap, out int width, out int height)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            bool loaded = tex.LoadImage(pngBytes);

            if (!loaded)
            {
                Object.Destroy(tex);
                packedBitmap = null;
                width = 0;
                height = 0;
                return false;
            }

            packedBitmap = BuildPackedBitmap(tex, out width, out height);
            Object.Destroy(tex);
            return true;
        }

        /// <summary>
        /// Builds the 8-byte "GS v 0"-shaped raster header for an image of the
        /// given pixel dimensions: 1D 76 30 00 + width-in-bytes (LE) + height-in-pixels (LE).
        /// </summary>
        public static byte[] BuildRasterHeader(int widthPx, int heightPx)
        {
            byte[] header = new byte[8];
            header[0] = 0x1D; header[1] = 0x76; header[2] = 0x30; header[3] = 0x00;

            int widthBytes = (widthPx + 7) / 8;
            header[4] = (byte)(widthBytes & 0xFF);
            header[5] = (byte)((widthBytes >> 8) & 0xFF);
            header[6] = (byte)(heightPx & 0xFF);
            header[7] = (byte)((heightPx >> 8) & 0xFF);
            return header;
        }

        /// <summary>
        /// Returns a new texture rotated 180 degrees — useful when the printer
        /// is mounted upside-down (e.g. built into a kiosk enclosure) so the
        /// physical output reads right-side up. Implemented as a straight
        /// reversal of the pixel array: for any row-major image, pixel i's
        /// 180-degree destination is (length-1-i), because rotating 180
        /// degrees maps (x,y) -> (width-1-x, height-1-y), and that maps index
        /// y*width+x -> (height-1-y)*width+(width-1-x) = (length-1)-(y*width+x).
        /// So it's origin-agnostic — works the same whether the source is
        /// top-left or bottom-left origin, no width/height bookkeeping needed.
        /// Always returns a brand-new Texture2D (RGB24), same convention as
        /// ResizeToWidth — never the original reference. Caller owns (and
        /// should Destroy) the returned texture.
        /// </summary>
        public static Texture2D Rotate180(Texture2D source)
        {
            int width = source.width;
            int height = source.height;
            Color32[] src = source.GetPixels32();
            Color32[] dst = new Color32[src.Length];

            int last = src.Length - 1;
            for (int i = 0; i < src.Length; i++)
            {
                dst[i] = src[last - i];
            }

            Texture2D rotated = new Texture2D(width, height, TextureFormat.RGB24, false);
            rotated.SetPixels32(dst);
            rotated.Apply();
            return rotated;
        }

        /// <summary>
        /// Resizes a texture to the given width, preserving aspect ratio and
        /// applying VerticalAspectCorrection to compensate for the printer's
        /// non-square dot pitch (see that property's doc comment). Defaults to
        /// whatever VerticalAspectCorrection is currently set to (e.g. by a
        /// debug-UI slider) unless a caller passes an explicit override.
        /// Always returns a brand-new Texture2D (RGB24) — never the original
        /// reference — so callers can freely mutate/destroy the result without
        /// risking the source texture (e.g. a screen capture still in use
        /// elsewhere).
        /// </summary>
        public static Texture2D ResizeToWidth(Texture2D source, int targetWidth, float? verticalAspectCorrection = null)
        {
            float correction = verticalAspectCorrection ?? VerticalAspectCorrection;
            int targetHeight = Mathf.RoundToInt(source.height * (targetWidth / (float)source.width) * correction);

            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight);
            Graphics.Blit(source, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D resized = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
            resized.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            resized.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            return resized;
        }

        /// <summary>
        /// Builds a smooth grayscale approximation: resized to the given width
        /// (defaults to the printer's native 384px) and converted to grayscale,
        /// but deliberately NOT thresholded or dithered — this will not show
        /// the actual dot pattern, density-cap tone shift, or anything else
        /// about the real print. For a UI preview that reflects the actual
        /// print output, use BuildDitheredPreview instead. This method is kept
        /// for callers that specifically want a smooth on-screen approximation
        /// rather than the literal print result.
        /// </summary>
        public static Texture2D BuildGrayscalePreview(Texture2D source, int targetWidth = PRINTER_WIDTH_PX)
        {
            Texture2D resized = ResizeToWidth(source, targetWidth);

            Color32[] pixels = resized.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                byte value = (byte)Mathf.RoundToInt(0.299f * pixels[i].r + 0.587f * pixels[i].g + 0.114f * pixels[i].b);
                pixels[i] = new Color32(value, value, value, 255);
            }
            resized.SetPixels32(pixels);
            resized.Apply();

            return resized;
        }

        public const float DEFAULT_MAX_AVERAGE_DENSITY = 0.42f;

        private static float _maxAverageDensity = DEFAULT_MAX_AVERAGE_DENSITY;

        /// <summary>
        /// Live, UI-drivable version of the density cap described on
        /// DitherWithDensityCap: the maximum average fraction of dots
        /// BuildPackedBitmap/BuildDitheredPreview are allowed to print black
        /// before they uniformly lighten the source to bring it under that
        /// cap. Lower this to make busy/dark prints come out cleaner (fewer
        /// dots, less "noise"); raise it to recover more midtone detail at
        /// the cost of a denser, dirtier-looking print.
        ///
        /// This is now a mutable, clamped (0.05–0.9) static property — same
        /// pattern as VerticalAspectCorrection — specifically so that
        /// BuildPackedBitmap's own default parameter can read it. That
        /// matters because BuildPackedBitmap is also reached indirectly via
        /// TryConvertPngToPackedBitmap, which has no maxAverageDensity
        /// parameter of its own; any bridge that decodes a print PNG through
        /// that helper (rather than calling BuildPackedBitmap directly) only
        /// ever sees this live value, never a per-call override. A debug-UI
        /// slider (see UIPeripagePrintDemo) can drive this directly and the
        /// printer-quality preview updates live, exactly like the vertical
        /// aspect correction slider.
        /// </summary>
        public static float MaxAverageDensity
        {
            get => _maxAverageDensity;
            set => _maxAverageDensity = Mathf.Clamp(value, 0.05f, 0.9f);
        }

        /// <summary>
        /// Default blank paper fed after the image, in mm, purely so there's
        /// somewhere to cut without slicing into the photo itself. Feel free to
        /// override per-print via AppendBottomMargin's parameter.
        /// </summary>
        public const float DEFAULT_BOTTOM_CUT_MARGIN_MM = 8f;

        /// <summary>
        /// Returns a new texture with marginMm worth of blank (white) rows added
        /// below the source image — i.e. printed LAST, right where the paper
        /// gets cut, so the image itself never sits flush against the cut line.
        /// Call this AFTER ResizeToWidth (so marginMm converts using the same
        /// per-row physical pitch the resized image is already using) and
        /// BEFORE EncodeToPNG/dithering, so the blank rows ride along through
        /// whatever bridge-side conversion happens next without any bridge
        /// needing to know about margins at all.
        ///
        /// Always returns a brand-new Texture2D (RGB24), same convention as
        /// ResizeToWidth — never the original reference — so callers can
        /// destroy it freely without touching the source.
        /// </summary>
        public static Texture2D AppendBottomMargin(Texture2D source, float marginMm = DEFAULT_BOTTOM_CUT_MARGIN_MM, float? verticalAspectCorrection = null)
        {
            int marginRows = MillimetersToDotRows(marginMm, verticalAspectCorrection);
            int width = source.width;
            int newHeight = source.height + marginRows;

            Texture2D result = new Texture2D(width, newHeight, TextureFormat.RGB24, false);

            Color32 white = new Color32(255, 255, 255, 255);
            Color32[] fill = new Color32[width * newHeight];
            for (int i = 0; i < fill.Length; i++) fill[i] = white;
            result.SetPixels32(fill);

            // Texture2D rows run bottom-to-top. BuildPrintOrderLuminance later
            // flips that to top-to-bottom print order, so whatever occupies the
            // texture's LOW rows (y = 0..marginRows-1) ends up printed LAST —
            // exactly the blank margin we want at the bottom of the physical
            // print. The source image goes above that, in the texture's
            // remaining upper rows.
            if (marginRows > 0)
            {
                Color32[] srcPixels = source.GetPixels32();
                result.SetPixels32(0, marginRows, width, source.height, srcPixels);
            }
            else
            {
                result.SetPixels32(source.GetPixels32());
            }
            result.Apply();

            return result;
        }

        /// <summary>
        /// Builds a luminance buffer in print order (row 0 = top of the
        /// printed image) from a decoded texture. Shared by BuildPackedBitmap
        /// and BuildDitheredPreview so both always dither identical data —
        /// the preview can never silently drift from what actually prints.
        /// </summary>
        private static float[,] BuildPrintOrderLuminance(Texture2D tex, int width, int height)
        {
            Color32[] pixels = tex.GetPixels32(); // bottom-left origin, row-major
            float[,] lum = new float[height, width];
            for (int y = 0; y < height; y++)
            {
                // Texture rows run bottom-to-top; the printer expects top-to-bottom.
                int srcRow = height - 1 - y;
                int rowStart = srcRow * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 c = pixels[rowStart + x];
                    lum[y, x] = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                }
            }
            return lum;
        }

        /// <summary>
        /// Applies the density cap and Atkinson dithering (see the kernel comment
        /// further down for why Atkinson over Floyd-Steinberg) to a print-order
        /// luminance buffer (mutated in place), returning which pixels print a
        /// dot. Shared by BuildPackedBitmap and BuildDitheredPreview.
        ///
        /// Density cap: measures the source's average darkness up front, and if
        /// it's over maxAverageDensity, proportionally SCALES DOWN every pixel's
        /// darkness (rather than uniformly adding brightness) so the post-dither
        /// average should land back at the cap. Error-diffusion dithering only
        /// redistributes each pixel's rounding error to its neighbours — it
        /// doesn't change how much total "ink" gets printed — so the post-dither
        /// black-dot ratio ends up close to the source's own average darkness. A
        /// busy dark/gray region can land well above what the thermal head can
        /// drive evenly line after line. ouor/my-bt-printers hit the same thing
        /// on the same printer family: an uncapped image averaged ~0.60 black-dot
        /// ratio and printed too dark/uneven, 0.30 was too light, 0.42 was what
        /// they settled on — used as the default here. Images already under the
        /// cap are untouched.
        ///
        /// Scaling is multiplicative, not a flat additive lift, and that choice
        /// matters: a flat "add X brightness to every pixel" approach can't fully
        /// desaturate a solid near-black region (a dark shirt, hair) without an
        /// enormous lift, since a lift derived from the AVERAGE is rarely big
        /// enough to push a near-0 luminance pixel across the 0.5 print
        /// threshold. Worse, that same lift pushes ordinary midtones right up
        /// next to 0.5 — exactly the input error diffusion handles worst,
        /// producing a noisier/busier stipple rather than a cleaner result. A
        /// lower cap could therefore visually look dirtier even as the true
        /// average dot count nudged down. Scaling every pixel's darkness by the
        /// same ratio instead (darkness *= cap/avg) shrinks near-black regions
        /// proportionally along with everything else, so a lower cap reliably
        /// means fewer, cleaner dots across the whole tonal range — not just a
        /// wash-out of midtones.
        ///
        /// Serpentine (boustrophedon) scanning: alternates scan direction every
        /// row instead of always going left-to-right, mirroring the diffusion
        /// kernel accordingly. Plain left-to-right-every-row error diffusion has
        /// a known directional bias — errors keep getting dragged the same way
        /// row after row, which shows up as visible diagonal/vertical "worm"
        /// streaking in flat or gradient regions rather than an even stipple.
        /// Alternating direction cancels that bias out; this is standard
        /// practice in production ditherers for exactly this reason.
        /// </summary>
        private static bool[,] DitherWithDensityCap(float[,] lum, int width, int height, float maxAverageDensity)
        {
            if (maxAverageDensity > 0f && maxAverageDensity < 1f)
            {
                float totalDarkness = 0f;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        totalDarkness += 1f - lum[y, x];

                float avgDarkness = totalDarkness / (width * height);

                if (avgDarkness > maxAverageDensity)
                {
                    float scale = maxAverageDensity / avgDarkness; // < 1, shrinks darkness proportionally
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            float darkness = 1f - lum[y, x];
                            lum[y, x] = 1f - darkness * scale;
                        }
                }
            }

            bool[,] printMask = new bool[height, width];

            // Atkinson dithering (Bill Atkinson's original Apple algorithm),
            // chosen over classic Floyd-Steinberg specifically for how it
            // handles solid dark regions (dark hair, dark clothing): F-S
            // diffuses 100% of each pixel's rounding error onward, so once a
            // region goes solid black the error just keeps compounding
            // forward through the same-value neighbours with nothing to
            // break it up — it reads as a flat block with little/no texture.
            // Atkinson only diffuses 6/8 (75%) of the error and simply drops
            // the rest, so error can't build up as aggressively in flat dark
            // regions; the result keeps a lighter, more even stipple in
            // shadows instead of crushing to solid black. This directly
            // targets the "picture A keeps texture in the dark shirt/hair,
            // picture B goes solid black there" comparison.
            //
            // Kernel (current pixel marked *, weights all 1/8):
            //     *  1/8 1/8
            //    1/8 1/8 1/8
            //         1/8
            // i.e. two pixels ahead on the current row, three pixels on the
            // row below (one trailing-diagonal, one directly below, one
            // forward-diagonal), and one pixel two rows below. Mirrored
            // left/right for serpentine scanning, same as before.
            for (int y = 0; y < height; y++)
            {
                bool leftToRight = (y & 1) == 0;
                int xStart = leftToRight ? 0 : width - 1;
                int xEnd = leftToRight ? width : -1;
                int xStep = leftToRight ? 1 : -1;
                int xAhead = leftToRight ? 1 : -1;   // this row's travel direction
                int xBehind = -xAhead;                // opposite of travel direction

                for (int x = xStart; x != xEnd; x += xStep)
                {
                    float oldValue = lum[y, x];
                    bool printDot = oldValue < 0.5f; // dark -> heat this dot
                    float newValue = printDot ? 0f : 1f;
                    float quantError = (oldValue - newValue) / 8f; // Atkinson: each of 6 taps gets 1/8; 2/8 (25%) is simply dropped
                    printMask[y, x] = printDot;

                    int xForward1 = x + xAhead;
                    int xForward2 = x + 2 * xAhead;
                    int xTrailing = x + xBehind;

                    if (xForward1 >= 0 && xForward1 < width) lum[y, xForward1] += quantError;
                    if (xForward2 >= 0 && xForward2 < width) lum[y, xForward2] += quantError;
                    if (y + 1 < height)
                    {
                        if (xTrailing >= 0 && xTrailing < width) lum[y + 1, xTrailing] += quantError;
                        lum[y + 1, x] += quantError;
                        if (xForward1 >= 0 && xForward1 < width) lum[y + 1, xForward1] += quantError;
                    }
                    if (y + 2 < height) lum[y + 2, x] += quantError;
                }
            }

            return printMask;
        }

        /// <summary>
        /// Converts a decoded Texture2D into the printer's native 1-bit-per-pixel
        /// packed row format (MSB first, bit=1 means "heat/print this dot").
        /// Uses Atkinson error-diffusion dithering (serpentine-scanned,
        /// density-capped — see DitherWithDensityCap) rather than a flat 50%
        /// threshold, which would lose all midtone/gradient detail.
        /// </summary>
        public static byte[] BuildPackedBitmap(Texture2D tex, out int width, out int height, float? maxAverageDensity = null)
        {
            float density = maxAverageDensity ?? MaxAverageDensity;

            width = tex.width;
            height = tex.height;
            int bytesPerRow = (width + 7) / 8;
            byte[] result = new byte[bytesPerRow * height];

            float[,] lum = BuildPrintOrderLuminance(tex, width, height);
            bool[,] printMask = DitherWithDensityCap(lum, width, height, density);

            for (int y = 0; y < height; y++)
            {
                int destRowStart = y * bytesPerRow;
                for (int x = 0; x < width; x++)
                {
                    if (!printMask[y, x]) continue;
                    int byteIndex = destRowStart + (x >> 3);
                    int bitIndex = 7 - (x & 7); // MSB first
                    result[byteIndex] |= (byte)(1 << bitIndex);
                }
            }

            return result;
        }

        /// <summary>
        /// Builds a Texture2D showing exactly what BuildPackedBitmap will send
        /// to the printer — same resize, same density cap, same dithering —
        /// rather than a smooth grayscale approximation. Use this for any UI
        /// preview panel that should reflect the real black/white print result
        /// (dot pattern, density-cap tone shift, everything) instead of
        /// BuildGrayscalePreview's continuous-tone approximation. Resizes a
        /// copy internally; does not mutate or destroy source. Caller owns
        /// (and should Destroy) the returned texture.
        /// </summary>
        public static Texture2D BuildDitheredPreview(Texture2D source, int targetWidth = PRINTER_WIDTH_PX, float? maxAverageDensity = null)
        {
            float density = maxAverageDensity ?? MaxAverageDensity;

            Texture2D resized = ResizeToWidth(source, targetWidth);
            int width = resized.width;
            int height = resized.height;

            float[,] lum = BuildPrintOrderLuminance(resized, width, height);
            bool[,] printMask = DitherWithDensityCap(lum, width, height, density);

            Color32[] previewPixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                // Convert print-order (row 0 = top) back to the texture's
                // bottom-left-origin layout so it displays right-side up.
                int texRow = height - 1 - y;
                int rowStart = texRow * width;
                for (int x = 0; x < width; x++)
                {
                    byte v = printMask[y, x] ? (byte)0 : (byte)255;
                    previewPixels[rowStart + x] = new Color32(v, v, v, 255);
                }
            }

            resized.SetPixels32(previewPixels);
            resized.Apply();
            return resized;
        }
    }
}