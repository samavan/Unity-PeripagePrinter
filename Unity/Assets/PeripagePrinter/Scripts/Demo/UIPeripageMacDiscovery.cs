using TMPro;
using UnityEngine;
using UnityEngine.UI;
using TechArt.Module.Peripage;

namespace TechArt.Module.Peripage.Demo
{
    /// <summary>
    /// Minimal UI glue for PeripageMacDeviceDiscovery. Not styled — just wires
    /// up the 5-step flow: scan, list devices, select, confirm, connect.
    /// Swap in your kiosk's real UI once the flow itself works.
    ///
    /// Expected hierarchy:
    ///   - deviceListParent: empty RectTransform, gets one item per device
    ///   - deviceButtonPrefab: a prefab with a UIPeripageDeviceListItem
    ///     component (Button + child TextMeshProUGUI), one instantiated per device
    ///   - refreshButton, okButton: standard UI Buttons
    ///   - statusText: a TextMeshProUGUI for connection status
    /// </summary>
    public class UIPeripageMacDiscovery : MonoBehaviour
    {
        #region Inspector

        [SerializeField] private PeripageMacDeviceDiscovery discovery;

        [Header("UI refs (assign in Inspector, or leave empty to auto-build a basic UI)")]
        [SerializeField] private RectTransform deviceListParent;
        [SerializeField] private UIPeripageDeviceListItem deviceButtonPrefab;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Button okButton;
        [SerializeField] private TextMeshProUGUI statusText;

        #endregion

        #region Private Fields

        private string _pendingSelectedAddress;

        #endregion

        #region Unity Lifecycle

        private void Start()
        {
            if (discovery == null)
            {
                discovery = FindObjectOfType<PeripageMacDeviceDiscovery>();
            }

            discovery.OnDevicesUpdated += RebuildDeviceList;
            discovery.OnConnected += () => SetStatus("Connected!");
            discovery.OnConnectFailed += (err) => SetStatus($"Connect failed: {err}");

            if (refreshButton != null)
            {
                refreshButton.onClick.AddListener(OnRefreshClicked);
            }

            if (okButton != null)
            {
                okButton.onClick.AddListener(OnOkClicked);
            }

            // Step 1: kick off an initial scan on start.
            OnRefreshClicked();
        }

        #endregion

        #region Button Handlers

        // Step 2: refresh button
        public void OnRefreshClicked()
        {
            SetStatus("Scanning...");
            discovery.Refresh();
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

        #endregion

        #region Private Methods

        // Step 3: called per-device-item when clicked
        private void OnDeviceClicked(string address, string name)
        {
            _pendingSelectedAddress = address;
            SetStatus($"Selected: {name} — press OK to connect");
        }

        private void RebuildDeviceList()
        {
            if (deviceListParent == null || deviceButtonPrefab == null)
            {
                return;
            }

            foreach (Transform child in deviceListParent)
            {
                Destroy(child.gameObject);
            }

            foreach (var device in discovery.devices)
            {
                UIPeripageDeviceListItem item = Instantiate(deviceButtonPrefab, deviceListParent);
                item.gameObject.SetActive(true);

                string address = device.address; // capture for closure
                string name = device.name;
                item.Setup(name, address, () => OnDeviceClicked(address, name));
            }

            SetStatus($"Found {discovery.devices.Count} device(s)");
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }

            Debug.Log($"[UIPeripageMacDiscovery] {message}");
        }

        #endregion
    }
}
