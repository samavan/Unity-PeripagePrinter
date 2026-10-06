using System.Collections;
using UnityEngine;

namespace PeripagePrinter.Runtime
{
    /// <summary>
    /// Real Mac implementation — talks directly to PeripageMacNative (the same
    /// native plugin PeripageMacDeviceDiscovery uses to scan/connect).
    ///
    /// This class only handles Mac-specific transport: chunked sending over
    /// Peripage_SendBytes, with the coroutine timing that requires. All protocol
    /// knowledge (packet framing, PNG decode, bit packing) lives in the shared,
    /// platform-agnostic PeripageBitmapProtocol class so a future iOS bridge (or
    /// any other platform whose native layer is just a raw byte pipe) can reuse
    /// it instead of duplicating this logic.
    ///
    /// Only functional in the Editor on Mac or in a Mac standalone build — see
    /// PeripagePrinterManager.InitBridge() for the platform switch that picks
    /// this over the mock/Android bridges.
    /// </summary>
    public class PeripageMacBridge : IPeripageBridge
    {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        private readonly PeripagePrinterManager _manager;

        public PeripageMacBridge(PeripagePrinterManager manager)
        {
            _manager = manager;
        }

        public void Connect(string macAddress) => PeripageMacNative.Peripage_Connect(macAddress);
        public void Disconnect() => PeripageMacNative.Peripage_Disconnect();
        public bool IsConnected() => PeripageMacNative.Peripage_IsConnected() == 1;

        public void PrintBitmap(byte[] pngBytes)
        {
            if (!IsConnected())
            {
                Debug.LogWarning("[PeripageMacBridge] Print requested but not connected — " +
                                  "run the scan/select/connect flow in PeripageMacDeviceDiscovery first.");
                _manager?.OnPrintFailedCallback("not connected (mac)");
                return;
            }

            if (_manager == null)
            {
                Debug.LogError("[PeripageMacBridge] No PeripagePrinterManager reference — can't run the print coroutine.");
                return;
            }

            _manager.StartCoroutine(PrintRoutine(pngBytes));
        }

        /// <summary>
        /// TEMPORARY DIAGNOSTIC: sends the smallest possible payload (the
        /// reference protocol's plain-text print mode: prefix + ASCII bytes,
        /// ONE native call, no image decode, no chunking) and times exactly how
        /// long that single blocking call takes. Compare this timing against
        /// the multi-minute hang on the full raster print:
        ///   - Fast (well under a second) -> the native layer is fine for small
        ///     calls; the problem is specific to the raster path (size, chunk
        ///     count, or chunking approach itself).
        ///   - Also slow/hangs -> the problem is upstream of the image data
        ///     entirely (connection health, the native plugin, or the OS
        ///     Bluetooth stack), not the bitmap protocol code.
        /// </summary>
        public bool PrintTestText(string text = "TEST")
        {
            if (!IsConnected())
            {
                Debug.LogWarning("[PeripageMacBridge] Test print requested but not connected.");
                return false;
            }

            byte[] textBytes = System.Text.Encoding.ASCII.GetBytes(text + "\n");
            byte[] payload = new byte[PeripageBitmapProtocol.PRINT_PREFIX.Length + textBytes.Length];
            System.Array.Copy(PeripageBitmapProtocol.PRINT_PREFIX, 0, payload, 0, PeripageBitmapProtocol.PRINT_PREFIX.Length);
            System.Array.Copy(textBytes, 0, payload, PeripageBitmapProtocol.PRINT_PREFIX.Length, textBytes.Length);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool ok = PeripageMacNative.Peripage_SendBytes(payload, payload.Length) == 1;
            stopwatch.Stop();

            Debug.Log($"[PeripageMacBridge] PrintTestText: sent {payload.Length} bytes in " +
                      $"{stopwatch.ElapsedMilliseconds}ms, native call returned {(ok ? "success" : "FAILURE")}.");
            return ok;
        }

        private IEnumerator PrintRoutine(byte[] pngBytes)
        {
            bool decoded = PeripageBitmapProtocol.TryConvertPngToPackedBitmap(
                pngBytes, out byte[] packedBitmap, out int width, out int height);

            if (!decoded)
            {
                Debug.LogWarning("[PeripageMacBridge] Failed to decode PNG bytes for printing.");
                _manager.OnPrintFailedCallback("failed to decode image (mac)");
                yield break;
            }

            if (width != PeripageBitmapProtocol.PRINTER_WIDTH_PX)
            {
                Debug.LogWarning($"[PeripageMacBridge] Image width {width}px != printer width " +
                                  $"{PeripageBitmapProtocol.PRINTER_WIDTH_PX}px — PeripagePrinterManager " +
                                  "should already have resized it. Printing anyway.");
            }

            bool ok = true;
            ok &= SendChunk(PeripageBitmapProtocol.PRINT_PREFIX);
            ok &= SendChunk(PeripageBitmapProtocol.PRINT_PADDING);
            ok &= SendChunk(PeripageBitmapProtocol.BuildRasterHeader(width, height));

            // Chunk in whole rows (one row per write, matching the
            // linglingltd/peripage-a6-control reference for this exact
            // hardware), not a flat byte count: Peripage_SendBytes is a
            // discrete write per call (BLE GATT under the hood, not a
            // continuous RFCOMM stream), so a chunk boundary that lands
            // mid-row desyncs the printer's row framing and the
            // misalignment compounds down the image. See
            // PeripageBitmapProtocol.GetRowAlignedChunkSize for details.
            int bytesPerRow = (width + 7) / 8;
            int chunkSize = PeripageBitmapProtocol.GetRowAlignedChunkSize(bytesPerRow);
            for (int offset = 0; offset < packedBitmap.Length && ok; offset += chunkSize)
            {
                int len = Mathf.Min(chunkSize, packedBitmap.Length - offset);
                byte[] chunk = new byte[len];
                System.Array.Copy(packedBitmap, offset, chunk, 0, len);
                ok &= SendChunk(chunk);
                yield return new WaitForSeconds(PeripageBitmapProtocol.RECOMMENDED_CHUNK_DELAY);
            }

            ok &= SendChunk(PeripageBitmapProtocol.PRINT_FOOTER);

            if (ok)
            {
                Debug.Log($"[PeripageMacBridge] Sent {width}x{height} bitmap to printer.");
                _manager.OnPrintCompleteCallback("");
            }
            else
            {
                Debug.LogWarning("[PeripageMacBridge] One or more SendBytes calls failed during print.");
                _manager.OnPrintFailedCallback("SendBytes failed partway through print (mac native)");
            }
        }

        private bool SendChunk(byte[] data)
        {
            return PeripageMacNative.Peripage_SendBytes(data, data.Length) == 1;
        }
#else
        public PeripageMacBridge(PeripagePrinterManager manager) { }
        public void Connect(string macAddress) { }
        public void Disconnect() { }
        public bool IsConnected() => false;
        public void PrintBitmap(byte[] imageBytes) { }
#endif
    }
}
