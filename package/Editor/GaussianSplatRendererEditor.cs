// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using GaussianSplatRenderer = GaussianSplatting.Runtime.GaussianSplatRenderer;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer))]
    [CanEditMultipleObjects]
    public class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        SerializedProperty m_PropSourcePath;
        SerializedProperty m_PropRenderOrder;
        SerializedProperty m_PropSplatScale;
        SerializedProperty m_PropOpacityScale;
        SerializedProperty m_PropSHOrder;

        readonly Utils.FilePickerControl m_FilePicker = new();
        int m_CameraIndex = 0;
        bool m_WasLoading;

        public void OnEnable()
        {
            m_PropSourcePath = serializedObject.FindProperty("m_SourcePath");
            m_PropRenderOrder = serializedObject.FindProperty("m_RenderOrder");
            m_PropSplatScale = serializedObject.FindProperty("m_SplatScale");
            m_PropOpacityScale = serializedObject.FindProperty("m_OpacityScale");
            m_PropSHOrder = serializedObject.FindProperty("m_SHOrder");
        }

        public override bool RequiresConstantRepaint() => ((GaussianSplatRenderer)target).isLoading || m_WasLoading;

        public override void OnInspectorGUI()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs)
                return;

            serializedObject.Update();
            var source = m_PropSourcePath;
            string sourcePath = source.stringValue;
            GUILayout.Label("StreamingAssets Source File", EditorStyles.boldLabel);
            Rect rect = EditorGUILayout.GetControlRect();
            EditorGUI.showMixedValue = source.hasMultipleDifferentValues;
            string path = m_FilePicker.PathFieldGUI(rect, new GUIContent("PLY / SPZ"), sourcePath);
            EditorGUI.showMixedValue = false;
            if (path != sourcePath)
            {
                sourcePath = path;
                source.stringValue = sourcePath;
            }
            if (!source.hasMultipleDifferentValues && string.IsNullOrWhiteSpace(sourcePath))
                EditorGUILayout.HelpBox("Gaussian Splat source file is not assigned", MessageType.Error);
            else if (!source.hasMultipleDifferentValues)
            {
                try
                {
                    string full = GaussianSplatReader.ResolvePath(Application.streamingAssetsPath, sourcePath);
                    if (!File.Exists(full)) EditorGUILayout.HelpBox("Source file is missing: " + full, MessageType.Warning);
                }
                catch (Exception e) { EditorGUILayout.HelpBox(e.Message, MessageType.Error); }
            }

            EditorGUILayout.Space();
            GUILayout.Label("Render Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderOrder);
            EditorGUILayout.PropertyField(m_PropSplatScale);
            EditorGUILayout.PropertyField(m_PropOpacityScale);
            EditorGUILayout.PropertyField(m_PropSHOrder);

            EditorGUILayout.Space();
            bool validAndEnabled = gs && gs.enabled && gs.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(sourcePath);
            if (validAndEnabled && !gs.HasValidRenderResources)
            {
                EditorGUILayout.HelpBox("Shader resources are not set up", MessageType.Error);
                validAndEnabled = false;
            }

            bool sourceChanged = false;
            foreach (var obj in targets)
                sourceChanged |= !source.hasMultipleDifferentValues && ((GaussianSplatRenderer)obj).m_SourcePath != sourcePath;
            serializedObject.ApplyModifiedProperties();
            if (GUILayout.Button("Reload") || sourceChanged)
                foreach (var obj in targets) ((GaussianSplatRenderer)obj).Reload();
            if (validAndEnabled && targets.Length == 1)
            {
                if (gs.isLoading) EditorGUILayout.LabelField("Loading...");
                else if (!string.IsNullOrEmpty(gs.loadError)) EditorGUILayout.HelpBox(gs.loadError, MessageType.Error);
                else if (gs.HasValidRenderSetup)
                {
                    EditCameras(gs);
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Splats", $"{gs.splatCount:N0}");
                    EditorGUILayout.LabelField("SH Degree", gs.sourceSHDegree.ToString());
                    int coefficients = (gs.sourceSHDegree + 1) * (gs.sourceSHDegree + 1) - 1;
                    int stride = 32 + (coefficients == 0 ? 0 : 4 + coefficients * 4);
                    EditorGUILayout.LabelField("Splat Data", $"{stride} bytes/splat ({(long)stride * gs.splatCount / (1024.0 * 1024):N1} MiB)");
                }
            }
            // Keep requesting a repaint until the completed state has replaced "Loading...".
            if (Event.current.type == EventType.Repaint)
                m_WasLoading = gs.isLoading;
        }


        void EditCameras(GaussianSplatRenderer gs)
        {
            var cameras = gs.cameras;
            if (cameras != null && cameras.Length != 0)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Cameras", EditorStyles.boldLabel);
                var camIndex = EditorGUILayout.IntSlider("Camera", m_CameraIndex, 0, cameras.Length - 1);
                camIndex = math.clamp(camIndex, 0, cameras.Length - 1);
                if (camIndex != m_CameraIndex)
                {
                    m_CameraIndex = camIndex;
                    gs.ActivateCamera(camIndex);
                }
            }
        }

        bool HasFrameBounds()
        {
            return true;
        }

        Bounds OnGetFrameBounds()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs || !gs.HasValidRenderSetup)
                return new Bounds(Vector3.zero, Vector3.one);
            Bounds bounds = gs.localBounds;
            bounds.extents *= 0.7f;
            return TransformBounds(gs.transform, bounds);
        }

        static Bounds TransformBounds(Transform tr, Bounds bounds)
        {
            var center = tr.TransformPoint(bounds.center);

            var ext = bounds.extents;
            var axisX = tr.TransformVector(ext.x, 0, 0);
            var axisY = tr.TransformVector(0, ext.y, 0);
            var axisZ = tr.TransformVector(0, 0, ext.z);

            // sum their absolute value to get the world extents
            ext.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            ext.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            ext.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

            return new Bounds { center = center, extents = ext };
        }
    }
}
