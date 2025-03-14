using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;

namespace UnityEngine.XR.Interaction.Toolkit.Samples.DeviceSimulator
{
    [RequireComponent(typeof(XRDeviceSimulatorUI))]
    class XRDeviceSimulatorHandsUI : MonoBehaviour
    {
        [Serializable]
        class HandExpressionUI
        {
            [SerializeField]
            Sprite m_Sprite;
            [SerializeField]
            Image m_ButtonImage;
            [SerializeField]
            Image m_Icon;
            [SerializeField]
            Text m_BindText;
            [SerializeField]
            Text m_TitleText;

            InputAction m_Action;

            public Sprite sprite
            {
                get => m_Sprite;
                set => m_Sprite = value;
            }

            public void Initialize(InputAction action, string name, Sprite icon)
            {
                m_Action = action;
                m_BindText.text = m_Action.controls[0].displayName;
                m_TitleText.text = $"[{name}]";
                if (icon != null)
                    m_Sprite = icon;
            }

            public void UpdateButtonVisuals(bool active, XRDeviceSimulatorUI uiManager)
            {
                UpdateButtonActive(active);

                Color color = active ? uiManager.enabledColor : uiManager.disabledColor;
                m_BindText.color = color;
                m_TitleText.color = color;
                m_Icon.color = color;

                m_Icon.transform.localScale = Vector3.one;
                m_Icon.sprite = uiManager.GetInputIcon(m_Action?.controls[0]);
            }

            public void SetButtonColor(Color color)
            {
                m_ButtonImage.color = color;
            }

            public void UpdateButtonActive(bool active)
            {
                m_BindText.gameObject.SetActive(active);
                m_TitleText.gameObject.SetActive(active);
                m_Icon.gameObject.SetActive(active);
            }
        }

        [Header("General")]

        [SerializeField]
        Image m_HandImage;

        [SerializeField]
        Sprite m_HandDefaultSprite;

        [SerializeField]
        List<HandExpressionUI> m_Expressions = new List<HandExpressionUI>();

        XRDeviceSimulatorUI m_MainUIManager;
        HandExpressionUI m_ActiveExpression;

        protected void Awake()
        {
            m_MainUIManager = GetComponent<XRDeviceSimulatorUI>();
        }

        internal void Initialize(XRDeviceSimulator simulator)
        {
            for (var index = 0; index < simulator.simulatedHandExpressions.Count; ++index)
            {
                var simulatedExpression = simulator.simulatedHandExpressions[index];
                if (index >= m_Expressions.Count)
                {
                    Debug.LogWarning("The Device Simulator has more expressions than the UI can display.", this);
                }
                else
                {
                    m_Expressions[index].Initialize(simulatedExpression.toggleAction, simulatedExpression.name, simulatedExpression.icon);
                }
            }

            m_HandImage.color = m_MainUIManager.disabledDeviceColor;
        }

        internal void SetActive(bool active, XRDeviceSimulator simulator)
        {
            foreach (var expression in m_Expressions)
            {
                expression.UpdateButtonVisuals(active, m_MainUIManager);
            }

            if (active)
            {
                foreach (var expression in m_Expressions)
                {
                    var isActiveExpression = m_ActiveExpression == expression;
                    expression.SetButtonColor(isActiveExpression ? m_MainUIManager.selectedColor : m_MainUIManager.buttonColor);
                }

                m_HandImage.color = m_MainUIManager.deviceColor;
            }
            else
            {
                var disabledSelectedColor = m_MainUIManager.selectedColor;
                disabledSelectedColor.a = 0.5f;
                foreach (var expression in m_Expressions)
                {
                    var isActiveExpression = m_ActiveExpression == expression;
                    expression.SetButtonColor(isActiveExpression ? disabledSelectedColor : m_MainUIManager.disabledButtonColor);
                    expression.UpdateButtonActive(isActiveExpression);
                }

                m_HandImage.color = m_MainUIManager.disabledDeviceColor;
            }
        }

        internal void ToggleExpression(XRDeviceSimulator.SimulatedHandExpression simulatedExpression, XRDeviceSimulator simulator)
        {
            // The index of the hand expression corresponds 1:1 with the index of the UI button
            var index = simulator.simulatedHandExpressions.IndexOf(simulatedExpression);
            if (index >= m_Expressions.Count)
            {
                Debug.LogWarning("The Device Simulator has more expressions than the UI can display.", this);
            }
            else if (index < 0)
            {
                Debug.LogError($"The Device Simulator tried to toggle {simulatedExpression.name} but it was not found in the list of simulated hand expressions, the UI can not be updated.", this);
            }
            else
            {
                ToggleExpression(m_Expressions[index]);
            }
        }

        void ToggleExpression(HandExpressionUI expression)
        {
            if (m_ActiveExpression == expression)
            {
                SetExpressionActiveStatus(false, expression);
                m_ActiveExpression = null;
                m_HandImage.sprite = m_HandDefaultSprite;
            }
            else
            {
                if (m_ActiveExpression != null)
                    SetExpressionActiveStatus(false, m_ActiveExpression);

                SetExpressionActiveStatus(true, expression);
                m_ActiveExpression = expression;
            }
        }

        void SetExpressionActiveStatus(bool isActive, HandExpressionUI expression)
        {
            expression.SetButtonColor(isActive ? m_MainUIManager.selectedColor : m_MainUIManager.buttonColor);
            if (isActive)
                m_HandImage.sprite = expression.sprite;
        }
    }
}
