using UnityEngine;
using UnityEngine.UI;
using TechArt.Module.Peripage;

namespace TechArt.Module.Peripage.Demo
{
    /// <summary>
    /// Minimal UI glue for PeripageMacDeviceDiscovery. Not styled — just wires
    /// up the 5-step flow you asked for so you have something to click through
    /// immediately. Swap in your kiosk's real UI once the flow itself works.
    ///
    /// Expected hierarchy (create these manually in the Scene, or let this
    /// script build a bare-bones one at runtime — see BuildFallbackUI below):
    ///   - deviceListParent: empty RectTransform, gets one button per device
    ///   - deviceButtonPrefab: a Button+Text prefab, one instantiated per device
    ///   - refreshButton, okButton: standard UI Buttons
    ///   - statusText: a Text/TMP element for connection status
    /// </summary>
    public class PeripageMacDiscoveryUI : MonoBehaviour
    {
        public PeripageMacDeviceDiscovery discovery;

        [Header("UI refs (assign in Inspector, or leave empty to auto-build a basic UI)")]
        public RectTransform deviceListParent;
        public Button deviceButtonPrefab;
        public Button refreshButton;
        public Button okButton;
        public Text statusText;

        private string _pendingSelectedAddress;

        void Start()
        {
            if (discovery == null) discovery = FindObjectOfType<PeripageMacDeviceDiscovery>();

            discovery.OnDevicesUpdated += RebuildDeviceList;
            discovery.OnConnected += () => SetStatus("Connected!");
            discovery.OnConnectFailed += (err) => SetStatus($"Connect failed: {err}");

            if (refreshButton != null) refreshButton.onClick.AddListener(OnRefreshClicked);
            if (okButton != null) okButton.onClick.AddListener(OnOkClicked);

            // Step 1: kick off an initial scan on start.
            OnRefreshClicked();
        }

        // Step 2: refresh button
        public void OnRefreshClicked()
        {
            SetStatus("Scanning...");
            discovery.Refresh();
        }

        // Step 3: called per-device-button when clicked
        private void OnDeviceClicked(string address, string name)
        {
            _pendingSelectedAddress = address;
            SetStatus($"Selected: {name} — press OK to connect");
        }

        // Step 4/5: OK button confirms selection and connects
        public void OnOkClicked()
        {
            if (string.IsNullOrEmpty(_pendingSelectedAddress))
            {
                SetStatus("Select a device first");
                return;
            }
            discovery.SelectDevice(_pendingSelectedAddress);
            SetStatus("Connecting...");
            discovery.ConfirmAndConnect();
        }

        private void RebuildDeviceList()
        {
            if (deviceListParent == null || deviceButtonPrefab == null) return;

            foreach (Transform child in deviceListParent)
            {
                Destroy(child.gameObject);
            }

            foreach (var device in discovery.devices)
            {
                Button btn = Instantiate(deviceButtonPrefab, deviceListParent);
                btn.gameObject.SetActive(true);
                var label = btn.GetComponentInChildren<Text>();
                if (label != null) label.text = $"{device.name} ({device.address})";

                string address = device.address; // capture for closure
                string name = device.name;
                btn.onClick.AddListener(() => OnDeviceClicked(address, name));
            }

            SetStatus($"Found {discovery.devices.Count} device(s)");
        }

        private void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message;
            Debug.Log($"[PeripageMacDiscoveryUI] {message}");
        }
    }
}
