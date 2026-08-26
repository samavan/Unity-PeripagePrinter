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
    /// The image data (step 4) should be sent to the printer in small chunks
    /// (RECOMMENDED_CHUNK_SIZE) with a short delay between them
    /// (RECOMMENDED_CHUNK_DELAY) — sending it all in one call risks overrunning
    /// the printer's Bluetooth receive buffer. Chunking/timing is left to each
    /// bridge since it depends on how that platform's transport is invoked
    /// (e.g. from a coroutine on Mac).
    /// </summary>
    public static class PeripageBitmapProtocol
    {
        public const int PRINTER_WIDTH_PX = 384;

        public const int RECOMMENDED_CHUNK_SIZE = 122;      // matches the reference implementation's write chunk size
        public const float RECOMMENDED_CHUNK_DELAY = 0.02f; // 20ms between chunks, ditto

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
        /// Resizes a texture to the given width, preserving aspect ratio. Always
        /// returns a brand-new Texture2D (RGB24) — never the original reference —
        /// so callers can freely mutate/destroy the result without risking the
        /// source texture (e.g. a screen capture still in use elsewhere).
        /// </summary>
        public static Texture2D ResizeToWidth(Texture2D source, int targetWidth)
        {
            int targetHeight = Mathf.RoundToInt(source.height * (targetWidth / (float)source.width));

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
        /// Builds a "what the printer roughly sees" preview: resized to the
        /// given width (defaults to the printer's native 384px) and converted
        /// to grayscale using the same luminance weights BuildPackedBitmap uses
        /// for dithering. Deliberately NOT thresholded or dithered — this is a
        /// smooth gradient preview for on-screen display, not the actual print
        /// data. Use BuildPackedBitmap for the real 1bpp dithered print output.
        /// This is the single place both the demo preview UI and any other
        /// caller should go through, so the preview's grayscale math can never
        /// drift out of sync with the print math.
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

        /// <summary>
        /// Converts a decoded Texture2D into the printer's native 1-bit-per-pixel
        /// packed row format (MSB first, bit=1 means "heat/print this dot").
        ///
        /// Uses Floyd-Steinberg error-diffusion dithering rather than a flat
        /// 50% threshold. A flat threshold makes every pixel lighter than
        /// middle-gray vanish entirely (no dot), which loses virtually all
        /// midtone detail — e.g. a light-gray gradient prints as almost
        /// nothing. Dithering instead pushes each pixel's rounding error onto
        /// its neighbours, so midtones come out as a dot pattern (halftone-style)
        /// that approximates the gray value instead of disappearing. This
        /// matches the reference implementation's ImageOps.invert() + convert("1")
        /// behaviour in spirit (dark source -> printed dot) but preserves
        /// gradient detail the flat threshold was throwing away.
        /// </summary>
        public static byte[] BuildPackedBitmap(Texture2D tex, out int width, out int height)
        {
            width = tex.width;
            height = tex.height;
            int bytesPerRow = (width + 7) / 8;
            byte[] result = new byte[bytesPerRow * height];

            Color32[] pixels = tex.GetPixels32(); // bottom-left origin, row-major

            // Luminance buffer in print order (row 0 = top of the printed image,
            // matching the top-to-bottom flip done below), so error diffusion
            // walks the image in the same order it'll actually be printed.
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

            for (int y = 0; y < height; y++)
            {
                int destRowStart = y * bytesPerRow;
                for (int x = 0; x < width; x++)
                {
                    float oldValue = lum[y, x];
                    bool printDot = oldValue < 0.5f; // dark -> heat this dot
                    float newValue = printDot ? 0f : 1f;
                    float quantError = oldValue - newValue;

                    if (printDot)
                    {
                        int byteIndex = destRowStart + (x >> 3);
                        int bitIndex = 7 - (x & 7); // MSB first
                        result[byteIndex] |= (byte)(1 << bitIndex);
                    }

                    // Standard Floyd-Steinberg error distribution: 7/16 right,
                    // 3/16 below-left, 5/16 below, 1/16 below-right.
                    if (x + 1 < width) lum[y, x + 1] += quantError * (7f / 16f);
                    if (y + 1 < height)
                    {
                        if (x - 1 >= 0) lum[y + 1, x - 1] += quantError * (3f / 16f);
                        lum[y + 1, x] += quantError * (5f / 16f);
                        if (x + 1 < width) lum[y + 1, x + 1] += quantError * (1f / 16f);
                    }
                }
            }

            return result;
        }
    }
}
