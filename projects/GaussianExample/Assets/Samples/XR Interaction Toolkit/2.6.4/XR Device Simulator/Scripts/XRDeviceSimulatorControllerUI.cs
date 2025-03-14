using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;

namespace UnityEngine.XR.Interaction.Toolkit.Samples.DeviceSimulator
{
    [RequireComponent(typeof(XRDeviceSimulatorUI))]
    class XRDeviceSimulatorControllerUI : MonoBehaviour
    {
        [Header("General")]

        [SerializeField]
        Image m_ControllerImage;

        [SerializeField]
        Image m_ControllerOverlayImage;

        [Header("Primary Button")]

        [SerializeField]
        Image m_PrimaryButtonImage;

        [SerializeField]
        Text m_PrimaryButtonText;

        [SerializeField]
        Image m_PrimaryButtonIcon;

        [Header("Secondary Button")]

        [SerializeField]
        Image m_SecondaryButtonImage;

        [SerializeField]
        Text m_SecondaryButtonText;

        [SerializeField]
        Image m_SecondaryButtonIcon;

        [Header("Trigger")]

        [SerializeField]
        Image m_TriggerButtonImage;

        [SerializeField]
        Text m_TriggerButtonText;

        [SerializeField]
        Image m_TriggerButtonIcon;

        [Header("Grip")]

        [SerializeField]
        Image m_GripButtonImage;

        [SerializeField]
        Text m_GripButtonText;

        [SerializeField]
        Image m_GripButtonIcon;

        [Header("Thumbstick")]

        [SerializeField]
        Image m_ThumbstickButtonImage;

        [SerializeField]
        Text m_ThumbstickButtonText;

        [SerializeField]
        Image m_ThumbstickButtonIcon;

        [Header("Menu")]

        [SerializeField]
        Image m_MenuButtonImage;

        [SerializeField]
        Text m_MenuButtonText;

        [SerializeField]
        Image m_MenuButtonIcon;

        XRDeviceSimulatorUI m_MainUIManager;

        bool m_PrimaryButtonActivated;
        bool m_SecondaryButtonActivated;
        bool m_TriggerActivated;
        bool m_GripActivated;
        bool m_MenuActivated;
        bool m_XAxisTranslateActivated;
        bool m_YAxisTranslateActivated;

        protected void Awake()
        {
            m_MainUIManager = GetComponent<XRDeviceSimulatorUI>();
        }

        internal void Initialize(XRDeviceSimulator simulator)
        {
            m_PrimaryButtonText.text = simulator.primaryButtonAction.action.controls[0].displayName;
            m_SecondaryButtonText.text = simulator.secondaryButtonAction.action.controls[0].displayName;
            m_GripButtonText.text = simulator.gripAction.action.controls[0].displayName;
            m_TriggerButtonText.text = simulator.triggerAction.action.controls[0].displayName;
            m_MenuButtonText.text = simulator.menuAction.action.controls[0].displayName;

            var disabledImgColor = m_MainUIManager.disabledColor;
            m_ThumbstickButtonImage.color = disabledImgColor;
            m_ControllerImage.color = m_MainUIManager.disabledDeviceColor;
            m_ControllerOverlayImage.color = disabledImgColor;
        }

        internal void SetAsActiveController(bool active, XRDeviceSimulator simulator, bool isRestingHand = false)
        {
            var controls = isRestingHand ?
                simulator.restingHandAxis2DAction.action.controls :
                simulator.axis2DAction.action.controls;

            m_ThumbstickButtonText.text = $"{controls[0].displayName}, {controls[1].displayName}, {controls[2].displayName}, {controls[3].displayName}";

            UpdateButtonVisuals(active, m_PrimaryButtonIcon, m_PrimaryButtonText, simulator.primaryButtonAction.action.controls[0]);
            UpdateButtonVisuals(active, m_SecondaryButtonIcon, m_SecondaryButtonText, simulator.secondaryButtonAction.action.controls[0]);
            UpdateButtonVisuals(active, m_TriggerButtonIcon, m_TriggerButtonText, simulator.triggerAction.action.controls[0]);
            UpdateButtonVisuals(active, m_GripButtonIcon, m_GripButtonText, simulator.gripAction.action.controls[0]);
            UpdateButtonVisuals(active, m_MenuButtonIcon, m_MenuButtonText, simulator.menuAction.action.controls[0]);
            UpdateButtonVisuals(active || isRestingHand, m_ThumbstickButtonIcon, m_ThumbstickButtonText, simulator.axis2DAction.action.controls[0]);

            if (active)
            {
                UpdateButtonColor(m_PrimaryButtonImage, m_PrimaryButtonActivated);
                UpdateButtonColor(m_SecondaryButtonImage, m_SecondaryButtonActivated);
                UpdateButtonColor(m_TriggerButtonImage, m_TriggerActivated);
                UpdateButtonColor(m_GripButtonImage, m_GripActivated);
                UpdateButtonColor(m_MenuButtonImage, m_MenuActivated);
                UpdateButtonColor(m_ThumbstickButtonImage, m_XAxisTranslateActivated || m_YAxisTranslateActivated);

                m_ControllerImage.color = m_MainUIManager.deviceColor;
                m_ControllerOverlayImage.color = m_MainUIManager.enabledColor;
            }
            else
            {
                UpdateDisableControllerButton(m_PrimaryButtonActivated, m_PrimaryButtonImage, m_PrimaryButtonIcon, m_PrimaryButtonText);
                UpdateDisableControllerButton(m_SecondaryButtonActivated, m_SecondaryButtonImage, m_SecondaryButtonIcon, m_SecondaryButtonText);
                UpdateDisableControllerButton(m_TriggerActivated, m_TriggerButtonImage, m_TriggerButtonIcon, m_TriggerButtonText);
                UpdateDisableControllerButton(m_GripActivated, m_GripButtonImage, m_GripButtonIcon, m_GripButtonText);
                UpdateDisableControllerButton(m_MenuActivated, m_MenuButtonImage, m_MenuButtonIcon, m_MenuButtonText);

                if (!isRestingHand)
                    UpdateDisableControllerButton(m_XAxisTranslateActivated || m_YAxisTranslateActivated, m_ThumbstickButtonImage, m_ThumbstickButtonIcon, m_ThumbstickButtonText);
                else
                    m_ThumbstickButtonImage.color = m_MainUIManager.buttonColor;

                m_ControllerImage.color = m_MainUIManager.disabledDeviceColor;
                m_ControllerOverlayImage.color = m_MainUIManager.disabledColor;
            }
        }

        // This function keeps the button selected color active if the key if hold when the controller is disabled.
        // Other buttons are disabled to avoid adding extra noise.
        void UpdateDisableControllerButton(bool active, Image button, Image buttonIcon, Text buttonText)
        {
            if (active)
            {
                var tmpColor = m_MainUIManager.selectedColor;
                tmpColor.a = 0.5f;
                button.color = tmpColor;
                buttonText.gameObject.SetActive(true);
                buttonIcon.gameObject.SetActive(true);
            }
            else
            {
                button.color = m_MainUIManager.disabledButtonColor;
                buttonText.gameObject.SetActive(false);
                buttonIcon.gameObject.SetActive(false);
            }
        }

        void UpdateButtonVisuals(bool active, Image buttonIcon, Text buttonText, InputControl control)
        {
            buttonText.gameObject.SetActive(active);
            buttonIcon.gameObject.SetActive(active);

            var color = active ? m_MainUIManager.enabledColor : m_MainUIManager.disabledColor;
            buttonText.color = color;
            buttonIcon.color = color;

            buttonIcon.transform.localScale = Vector3.one;
            buttonIcon.sprite = m_MainUIManager.GetInputIcon(control);
            switch (control.name)
            {
                case "leftButton":
                    buttonText.text = "L Mouse";
                    buttonIcon.color = Color.white;
                    buttonIcon.transform.localScale = new Vector3(-1f, 1f, 1f);
                    break;
                case "rightButton":
                    buttonText.text = "R Mouse";
                    buttonIcon.color = Color.white;
                    break;
                default:
                    buttonIcon.sprite = m_MainUIManager.keyboardSprite;
                    break;
            }
        }

        void UpdateButtonColor(Image image, bool activated)
        {
            image.color = activated ? m_MainUIManager.selectedColor : m_MainUIManager.buttonColor;
        }

        internal void OnPrimaryButton(bool activated)
        {
            m_PrimaryButtonActivated = activated;
            UpdateButtonColor(m_PrimaryButtonImage, activated);
        }

        internal void OnSecondaryButton(bool activated)
        {
            m_SecondaryButtonActivated = activated;
            UpdateButtonColor(m_SecondaryButtonImage, activated);
        }

        internal void OnTrigger(bool activated)
        {
            m_TriggerActivated = activated;
            UpdateButtonColor(m_TriggerButtonImage, activated);
        }

        internal void OnGrip(bool activated)
        {
            m_GripActivated = activated;
            UpdateButtonColor(m_GripButtonImage, activated);
        }

        internal void OnMenu(bool activated)
        {
            m_MenuActivated = activated;
            UpdateButtonColor(m_MenuButtonImage, activated);
        }

        internal void OnXAxisTranslatePerformed(bool activated)
        {
            m_XAxisTranslateActivated = activated;
            UpdateButtonColor(m_ThumbstickButtonImage, activated);
        }

        internal void OnZAxisTranslatePerformed(bool activated)
        {
            m_YAxisTranslateActivated = activated;
            UpdateButtonColor(m_ThumbstickButtonImage, activated);
        }
    }
}
