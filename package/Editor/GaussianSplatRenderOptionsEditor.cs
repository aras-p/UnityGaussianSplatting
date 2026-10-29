// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderOptions))]
    [CanEditMultipleObjects]
    public class GaussianSplatRenderOptionsEditor : UnityEditor.Editor
    {
        SerializedProperty m_PropScript;
        SerializedProperty m_PropTransparency;
        SerializedProperty m_PropTemporalFilter;
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointSize;
        SerializedProperty m_PropSHOnly;

        void OnEnable()
        {
            m_PropScript = serializedObject.FindProperty("m_Script");
            m_PropTransparency = serializedObject.FindProperty("m_Transparency");
            m_PropTemporalFilter = serializedObject.FindProperty("m_TemporalFilter");
            m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PropPointSize = serializedObject.FindProperty("m_PointDisplaySize");
            m_PropSHOnly = serializedObject.FindProperty("m_SHOnly");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(m_PropScript);
            EditorGUILayout.PropertyField(m_PropTransparency);
            if (!m_PropTransparency.hasMultipleDifferentValues && m_PropTransparency.intValue == (int)TransparencyMode.Stochastic)
                EditorGUILayout.PropertyField(m_PropTemporalFilter);
            EditorGUILayout.PropertyField(m_PropRenderMode);
            if (!m_PropRenderMode.hasMultipleDifferentValues && m_PropRenderMode.intValue == (int)RenderMode.DebugPoints)
                EditorGUILayout.PropertyField(m_PropPointSize);
            EditorGUILayout.PropertyField(m_PropSHOnly);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
