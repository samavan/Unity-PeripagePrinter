using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TechArt.Module.Peripage.Demo
{
    /// <summary>
    /// Lives on the device button prefab (btnTemplate). Owns the references to
    /// its own child UI so list-building code never has to guess at structure
    /// via GetComponentInChildren — it just calls Setup().
    ///
    /// Prefab hierarchy expected:
    ///   btnTemplate (Button, this component)
    ///     └── txtDeviceName (TextMeshProUGUI)
    ///
    /// In the Inspector, drag the Button component (usually itself) into
    /// "Button" and the child TMP label into "Txt Device Name".
    /// </summary>
    public class UIPeripageDeviceListItem : MonoBehaviour
    {
        #region Inspector

        [SerializeField] private Button button;
        [SerializeField] private TextMeshProUGUI txtDeviceName;

        #endregion

        #region Private Fields

        private Action _onClicked;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // The Button lives on a child (e.g. btnUserPress), not necessarily on
            // this GameObject, so search children too — and include inactive ones,
            // since the template item's children may start disabled.
            if (button == null)
            {
                button = GetComponentInChildren<Button>(true);
            }

            button.onClick.AddListener(HandleClick);
        }

        #endregion

        #region Public Methods

        /// <summary>Populates the label and stores the click callback for this row.</summary>
        public void Setup(string deviceName, string address, Action onClicked)
        {
            // Three independent switches can hide/disable a UI element in Unity:
            // GameObject.activeSelf (the object), Behaviour.enabled (the
            // component), and — for anything clickable — the target Graphic
            // (e.g. the Button's Image) that the EventSystem raycasts against.
            // The template item can start with any combination of these off,
            // so force everything back on here rather than assuming one is enough.
            if (!txtDeviceName.gameObject.activeSelf)
            {
                txtDeviceName.gameObject.SetActive(true);
            }

            if (!txtDeviceName.enabled)
            {
                txtDeviceName.enabled = true;
            }

            if (!button.gameObject.activeSelf)
            {
                button.gameObject.SetActive(true);
            }

            if (!button.enabled)
            {
                button.enabled = true;
            }

            if (button.targetGraphic != null && !button.targetGraphic.enabled)
            {
                button.targetGraphic.enabled = true;
            }

            txtDeviceName.text = $"{deviceName} ({address})";
            _onClicked = onClicked;
        }

        #endregion

        #region Private Methods

        private void HandleClick()
        {
            _onClicked?.Invoke();
        }

        #endregion
    }
}
