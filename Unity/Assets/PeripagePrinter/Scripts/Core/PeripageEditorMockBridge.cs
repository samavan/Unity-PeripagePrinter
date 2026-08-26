using System.Collections;
using UnityEngine;

namespace TechArt.Module.Peripage
{
    /// <summary>
    /// Mock bridge used in the Editor (and optionally standalone Win/Mac builds)
    /// where there's no real Android Bluetooth stack to talk to. Simulates
    /// connect/print delays and outcomes so you can build and test the kiosk
    /// UI flow — button states, spinners, success/fail screens — without the
    /// physical printer or an Android build.
    ///
    /// Runs coroutines on the manager's MonoBehaviour since this class itself
    /// isn't one.
    /// </summary>
    public class PeripageEditorMockBridge : IPeripageBridge
    {
        private readonly MonoBehaviour _runner;
        private readonly PeripagePrinterManager _manager;
        private bool _connected;

        // Mock behaviour settings (set via object initializer from PeripagePrinterManager,
        // not exposed in the Inspector since this isn't a MonoBehaviour/serialized type).
        public float simulatedConnectDelay = 0.8f;
        public float simulatedPrintDelaySecondsPerRow = 0.01f; // rough "thermal head speed" feel
        public bool simulateConnectFailure = false;
        public bool simulatePrintFailure = false;

        public PeripageEditorMockBridge(MonoBehaviour runner, PeripagePrinterManager manager)
        {
            _runner = runner;
            _manager = manager;
        }

        public void Connect(string macAddress)
        {
            _runner.StartCoroutine(ConnectRoutine(macAddress));
        }

        private IEnumerator ConnectRoutine(string macAddress)
        {
            Debug.Log($"[EditorMock] Connecting to '{macAddress}'...");
            yield return new WaitForSeconds(simulatedConnectDelay);

            if (simulateConnectFailure)
            {
                Debug.LogWarning("[EditorMock] Simulated connect failure");
                _manager.OnConnectFailedCallback("simulated failure (editor mock)");
            }
            else
            {
                _connected = true;
                Debug.Log("[EditorMock] Connected");
                _manager.OnConnectedCallback(macAddress);
            }
        }

        public void Disconnect()
        {
            _connected = false;
            Debug.Log("[EditorMock] Disconnected");
            _manager.OnDisconnectedCallback("");
        }

        public bool IsConnected() => _connected;

        public void PrintBitmap(byte[] imageBytes)
        {
            _runner.StartCoroutine(PrintRoutine(imageBytes));
        }

        private IEnumerator PrintRoutine(byte[] imageBytes)
        {
            if (!_connected)
            {
                _manager.OnPrintFailedCallback("not connected (editor mock)");
                yield break;
            }

            Debug.Log($"[EditorMock] Printing image ({imageBytes.Length} bytes)...");

            // Rough stand-in for physical print time, since we no longer know
            // pixel height directly from raw PNG bytes without decoding them.
            float duration = Mathf.Clamp(imageBytes.Length / 20000f, 0.3f, 3f);
            yield return new WaitForSeconds(duration);

            if (simulatePrintFailure)
            {
                Debug.LogWarning("[EditorMock] Simulated print failure");
                _manager.OnPrintFailedCallback("simulated failure (editor mock)");
            }
            else
            {
                Debug.Log("[EditorMock] Print complete");
                // Also dumps a PNG preview to disk so you can actually see what
                // would have been printed — see PeripagePrinterManager.PrintPhoto.
                _manager.OnPrintCompleteCallback("");
            }
        }
    }
}
