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
        SerializedProperty m_TemporalFrameInfluence;
        SerializedProperty m_TemporalVarianceClampScale;
        SerializedProperty m_RenderMode;
        SerializedProperty m_PointDisplaySize;
        SerializedProperty m_SHOnly;

        public void OnEnable()
        {
            m_Transparency = serializedObject.FindProperty("m_Transparency");
            m_SortNthFrame = serializedObject.FindProperty("m_SortNthFrame");
            m_TemporalFrameInfluence = serializedObject.FindProperty("m_TemporalFrameInfluence");
            m_TemporalVarianceClampScale = serializedObject.FindProperty("m_TemporalVarianceClampScale");
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
            if (m_Transparency.intValue is (int) TransparencyMode.Stochastic)
            {
                EditorGUILayout.PropertyField(m_TemporalFrameInfluence);
                EditorGUILayout.PropertyField(m_TemporalVarianceClampScale);
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