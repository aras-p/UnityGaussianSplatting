// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatSettings))]
    [CanEditMultipleObjects]
    public class GaussianSplatSettingsEditor : UnityEditor.Editor
    {
        SerializedProperty m_Transparency;
        SerializedProperty m_SortNthFrame;
        SerializedProperty m_TemporalFilter;
        SerializedProperty m_FrameInfluence;
        SerializedProperty m_VarianceClampScale;
        SerializedProperty m_RenderMode;
        SerializedProperty m_PointDisplaySize;
        SerializedProperty m_SHOnly;

        public void OnEnable()
        {
            m_Transparency = serializedObject.FindProperty("m_Transparency");
            m_SortNthFrame = serializedObject.FindProperty("m_SortNthFrame");
            m_TemporalFilter = serializedObject.FindProperty("m_TemporalFilter");
            m_FrameInfluence = serializedObject.FindProperty("m_FrameInfluence");
            m_VarianceClampScale = serializedObject.FindProperty("m_VarianceClampScale");
            m_RenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PointDisplaySize = serializedObject.FindProperty("m_PointDisplaySize");
            m_SHOnly = serializedObject.FindProperty("m_SHOnly");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(m_Transparency);
            if (m_Transparency.intValue is (int) TransparencyMode.SortedBlended)
            {
                EditorGUILayout.PropertyField(m_SortNthFrame);
            }
            if (m_Transparency.intValue == (int) TransparencyMode.Stochastic)
            {
                EditorGUILayout.PropertyField(m_TemporalFilter);
                if (m_TemporalFilter.intValue != (int)TemporalFilter.None)
                {
                    EditorGUILayout.PropertyField(m_FrameInfluence);
                    EditorGUILayout.PropertyField(m_VarianceClampScale);
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("Debugging Tweaks", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_RenderMode);
            if (m_RenderMode.intValue is (int)DebugRenderMode.DebugPoints or (int)DebugRenderMode.DebugPointIndices)
                EditorGUILayout.PropertyField(m_PointDisplaySize);
            EditorGUILayout.PropertyField(m_SHOnly);

            serializedObject.ApplyModifiedProperties();
        }
    }
}