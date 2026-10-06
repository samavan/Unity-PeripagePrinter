using TMPro;
using UnityEngine;
using UnityEngine.UI;
using TechArt.Module.Peripage;

namespace TechArt.Module.Peripage.Demo
{
    /// <summary>
    /// Minimal UI glue for the device-discovery flow: scan/refresh, list
    /// devices, select, confirm, connect. Not styled — swap in your kiosk's
    /// real UI once the flow itself works.
    ///
    /// Platform-agnostic: talks only to IPeripageDeviceDiscovery, so it works
    /// against PeripageMacDeviceDiscovery on Mac or
    /// PeripageAndroidDeviceDiscovery on Android without caring which. Assign
    /// the right one for the current platform in "Discovery Source", or leave
    /// it empty to auto-pick one via FindObjectOfType at runtime. Windows (and
    /// anything else) has no implementation yet — logs a warning and disables
    /// the flow rather than crashing.
    ///
    /// Expected hierarchy (all assigned in the Inspector — there is no
    /// auto-build fallback):
    ///   - deviceListParent: empty RectTransform, gets one item per device
    ///   - deviceButtonPrefab: a prefab with a UIPeripageDeviceListItem
    ///     component (Button + child TextMeshProUGUI), one instantiated per device
    ///   - refreshButton, okButton: standard UI Buttons
    ///   - statusText: a TextMeshProUGUI for connection status
    ///   - txtPlatform: a TextMeshProUGUI showing which platform/backend is active
    /// </summary>
    public class UIPeripageDiscovery : MonoBehaviour
    {
        #region Inspector

        [Tooltip("PeripageMacDeviceDiscovery or PeripageAndroidDeviceDiscovery — " +
                 "must implement IPeripageDeviceDiscovery. Leave empty to auto-pick " +
                 "the right one for the current platform via FindObjectOfType.")]
        [SerializeField] private MonoBehaviour discoverySource;

        [Header("UI refs (assign in Inspector)")]
        [SerializeField] private RectTransform deviceListParent;
        [SerializeField] private UIPeripageDeviceListItem deviceButtonPrefab;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Button okButton;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI txtPlatform;

        #endregion

        #region Private Fields

        private IPeripageDeviceDiscovery _discovery;
        private string _pendingSelectedAddress;
        private bool _connecting;

        #endregion

        #region Unity Lifecycle

        private void Start()
        {
            SetPlatformLabel();

            _discovery = ResolveDiscovery();
            if (_discovery == null)
            {
                SetStatus("Printer discovery isn't supported on this platform yet.");
                if (refreshButton != null) refreshButton.interactable = false;
                if (okButton != null) okButton.interactable = false;
                return;
            }

            _discovery.OnDevicesUpdated += HandleDevicesUpdated;
            _discovery.OnConnected += HandleConnected;
            _discovery.OnConnectFailed += HandleConnectFailed;

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

        private void OnDestroy()
        {
            if (_discovery != null)
            {
                _discovery.OnDevicesUpdated -= HandleDevicesUpdated;
                _discovery.OnConnected -= HandleConnected;
                _discovery.OnConnectFailed -= HandleConnectFailed;
            }

            if (refreshButton != null) refreshButton.onClick.RemoveListener(OnRefreshClicked);
            if (okButton != null) okButton.onClick.RemoveListener(OnOkClicked);
        }

        #endregion

        #region Button Handlers

        // Step 2: refresh button
        public void OnRefreshClicked()
        {
            if (_discovery == null) return;

            SetStatus("Scanning...");
            _discovery.Refresh();
        }

        // Step 4/5: OK button confirms selection and connects
        public void OnOkClicked()
        {
            if (_discovery == null) return;

            if (_connecting)
            {
                SetStatus("Still connecting — please wait...");
                return;
            }

            if (string.IsNullOrEmpty(_pendingSelectedAddress))
            {
                SetStatus("Select a device first");
                return;
            }

            _connecting = true;
            SetOkInteractable(false);

            _discovery.SelectDevice(_pendingSelectedAddress);
            SetStatus("Connecting...");
            _discovery.ConfirmAndConnect();
        }

        #endregion

        #region Discovery Event Handlers

        /// <summary>
        /// Single place that reports scan results. Sets the status FIRST so it
        /// can never be skipped by the list-building code below. While a live
        /// scan is still running (Mac), IsScanning stays true and this reports
        /// progress; once it's done (always immediately on Android) it reports
        /// "Scan complete".
        /// </summary>
        private void HandleDevicesUpdated()
        {
            int count = _discovery.Devices.Count;

            if (_discovery.IsScanning)
            {
                SetStatus($"Scanning... {count} found so far");
            }
            else if (count == 0)
            {
                SetStatus("Scan complete: no devices found");
            }
            else
            {
                SetStatus($"Scan complete: {count} device(s) found — tap one, then OK");
            }

            RebuildDeviceList();
        }

        private void HandleConnected()
        {
            _connecting = false;
            SetOkInteractable(true);
            SetStatus("Connected!");
        }

        private void HandleConnectFailed(string error)
        {
            _connecting = false;
            SetOkInteractable(true);
            SetStatus($"Connect failed: {error}");
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Picks the right concrete discovery implementation for the current
        /// platform when none was assigned in the Inspector. Add further
        /// platforms here as they get an IPeripageDeviceDiscovery implementation.
        /// </summary>
        private IPeripageDeviceDiscovery ResolveDiscovery()
        {
            if (discoverySource is IPeripageDeviceDiscovery assigned)
            {
                return assigned;
            }

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            return FindObjectOfType<PeripageMacDeviceDiscovery>();
#elif UNITY_ANDROID
            return FindObjectOfType<PeripageAndroidDeviceDiscovery>();
#else
            Debug.LogWarning($"[UIPeripageDiscovery] No IPeripageDeviceDiscovery implementation for " +
                              $"{Application.platform} yet — Mac and Android only so far.");
            return null;
#endif
        }

        private void SetPlatformLabel()
        {
            if (txtPlatform == null) return;

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            txtPlatform.text = "Platform: macOS";
#elif UNITY_ANDROID
            txtPlatform.text = "Platform: Android";
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            txtPlatform.text = "Platform: Windows (not supported yet)";
            Debug.LogWarning("[UIPeripageDiscovery] Windows has no Peripage discovery implementation yet.");
#else
            txtPlatform.text = $"Platform: {Application.platform} (not supported yet)";
            Debug.LogWarning($"[UIPeripageDiscovery] {Application.platform} has no Peripage discovery implementation yet.");
#endif
        }

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
                Debug.LogWarning("[UIPeripageDiscovery] deviceListParent and/or deviceButtonPrefab " +
                                 "are not assigned in the Inspector — the device list can't be drawn.");
                return;
            }

            foreach (Transform child in deviceListParent)
            {
                Destroy(child.gameObject);
            }

            foreach (var device in _discovery.Devices)
            {
                UIPeripageDeviceListItem item = Instantiate(deviceButtonPrefab, deviceListParent);
                item.gameObject.SetActive(true);

                string address = device.address; // capture for closure
                string name = device.name;
                item.Setup(name, address, () => OnDeviceClicked(address, name));
            }
        }

        private void SetOkInteractable(bool value)
        {
            if (okButton != null) okButton.interactable = value;
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }

            Debug.Log($"[UIPeripageDiscovery] {message}");
        }

        #endregion
    }
}