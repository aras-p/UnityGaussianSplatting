// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

// ReSharper disable once CheckNamespace
namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer))]
    public class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        SerializedProperty m_PropAsset;
        SerializedProperty m_PropSplatScale;
        SerializedProperty m_PropOpacityScale;
        SerializedProperty m_PropSHOrder;
        SerializedProperty m_PropSortNthFrame;
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointDisplaySize;
        SerializedProperty m_PropCutouts;
        SerializedProperty m_PropShaderSplats;
        SerializedProperty m_PropShaderComposite;
        SerializedProperty m_PropShaderDebugPoints;
        SerializedProperty m_PropShaderDebugBoxes;
        SerializedProperty m_PropCSSplatUtilities;

        bool m_ResourcesExpanded = false;
        int m_CameraIndex = 0;

        static int s_EditStatsUpdateCounter = 0;

        static HashSet<GaussianSplatRendererEditor> s_AllEditors = new();

        public static void BumpGUICounter()
        {
            ++s_EditStatsUpdateCounter;
        }

        public static void RepaintAll()
        {
            foreach (var e in s_AllEditors)
                e.Repaint();
        }

        public void OnEnable()
        {
            m_PropAsset = serializedObject.FindProperty("m_Asset");
            m_PropSplatScale = serializedObject.FindProperty("m_SplatScale");
            m_PropOpacityScale = serializedObject.FindProperty("m_OpacityScale");
            m_PropSHOrder = serializedObject.FindProperty("m_SHOrder");
            m_PropSortNthFrame = serializedObject.FindProperty("m_SortNthFrame");
            m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PropPointDisplaySize = serializedObject.FindProperty("m_PointDisplaySize");
            m_PropCutouts = serializedObject.FindProperty("m_Cutouts");
            m_PropShaderSplats = serializedObject.FindProperty("m_ShaderSplats");
            m_PropShaderComposite = serializedObject.FindProperty("m_ShaderComposite");
            m_PropShaderDebugPoints = serializedObject.FindProperty("m_ShaderDebugPoints");
            m_PropShaderDebugBoxes = serializedObject.FindProperty("m_ShaderDebugBoxes");
            m_PropCSSplatUtilities = serializedObject.FindProperty("m_CSSplatUtilities");

            s_AllEditors.Add(this);
        }

        public void OnDisable()
        {
            s_AllEditors.Remove(this);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            GUILayout.Label("Data Asset", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropAsset);

            var gs = target as GaussianSplatRenderer;
            if (!gs || !gs.HasValidAsset)
            {
                var msg = gs.asset != null && gs.asset.m_FormatVersion != GaussianSplatAsset.kCurrentVersion
                    ? "Gaussian Splat asset version is not compatible, please recreate the asset"
                    : "Gaussian Splat asset is not assigned or is empty";
                EditorGUILayout.HelpBox(msg, MessageType.Error);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Render Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropSplatScale);
            EditorGUILayout.PropertyField(m_PropOpacityScale);
            EditorGUILayout.PropertyField(m_PropSHOrder);
            EditorGUILayout.PropertyField(m_PropSortNthFrame);

            EditorGUILayout.Space();
            GUILayout.Label("Debugging Tweaks", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderMode);
            if (m_PropRenderMode.intValue is (int)GaussianSplatRenderer.RenderMode.DebugPoints or (int)GaussianSplatRenderer.RenderMode.DebugPointIndices)
                EditorGUILayout.PropertyField(m_PropPointDisplaySize);

            EditorGUILayout.Space();
            m_ResourcesExpanded = EditorGUILayout.Foldout(m_ResourcesExpanded, "Resources", true, EditorStyles.foldoutHeader);
            if (m_ResourcesExpanded)
            {
                EditorGUILayout.PropertyField(m_PropShaderSplats);
                EditorGUILayout.PropertyField(m_PropShaderComposite);
                EditorGUILayout.PropertyField(m_PropShaderDebugPoints);
                EditorGUILayout.PropertyField(m_PropShaderDebugBoxes);
                EditorGUILayout.PropertyField(m_PropCSSplatUtilities);
            }
            bool validAndEnabled = gs && gs.enabled && gs.gameObject.activeInHierarchy && gs.HasValidAsset;
            if (validAndEnabled && !gs.HasValidRenderSetup)
            {
                EditorGUILayout.HelpBox("Shader resources are not set up", MessageType.Error);
                validAndEnabled = false;
            }

            if (validAndEnabled)
            {
                EditCameras(gs);
                EditGUI(gs);
            }

            serializedObject.ApplyModifiedProperties();
        }

        void EditCameras(GaussianSplatRenderer gs)
        {
            var asset = gs.asset;
            var cameras = asset.m_Cameras;
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

        void EditGUI(GaussianSplatRenderer gs)
        {
            ++s_EditStatsUpdateCounter;

            EditorGUILayout.Space(12f, true);
            GUILayout.Box(GUIContent.none, "sv_iconselector_sep", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            EditorGUILayout.Space();
            bool wasToolActive = ToolManager.activeToolType == typeof(GaussianSplatsTool);
            bool isToolActive = GUILayout.Toggle(wasToolActive, "Edit", EditorStyles.miniButton);
            if (!wasToolActive && isToolActive)
                ToolManager.SetActiveTool<GaussianSplatsTool>();
            if (wasToolActive && !isToolActive)
                Tools.current = Tool.View;

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Cutout"))
            {
                GaussianCutout cutout = ObjectFactory.CreateGameObject("GSCutout", typeof(GaussianCutout)).GetComponent<GaussianCutout>();
                Transform cutoutTr = cutout.transform;
                cutoutTr.SetParent(gs.transform, false);
                cutoutTr.localScale = (gs.asset.m_BoundsMax - gs.asset.m_BoundsMin) * 0.25f;
                gs.m_Cutouts ??= Array.Empty<GaussianCutout>();
                ArrayUtility.Add(ref gs.m_Cutouts, cutout);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
                Selection.activeGameObject = cutout.gameObject;
            }
            if (GUILayout.Button("Use All Cutouts"))
            {
                gs.m_Cutouts = FindObjectsByType<GaussianCutout>(FindObjectsSortMode.InstanceID);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }

            if (GUILayout.Button("No Cutouts"))
            {
                gs.m_Cutouts = Array.Empty<GaussianCutout>();
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(m_PropCutouts);
            EditorGUILayout.Space();

            bool hasCutouts = gs.m_Cutouts != null && gs.m_Cutouts.Length != 0;
            bool modifiedOrHasCutouts = gs.editModified || hasCutouts;
            bool displayEditTools = isToolActive || modifiedOrHasCutouts;

            if (displayEditTools)
            {
                var asset = gs.asset;
                EditorGUILayout.LabelField("Splats", $"{asset.m_SplatCount:N0}");
                EditorGUILayout.LabelField("Cut", $"{gs.editCutSplats:N0}");
                EditorGUILayout.LabelField("Deleted", $"{gs.editDeletedSplats:N0}");
                EditorGUILayout.LabelField("Selected", $"{gs.editSelectedSplats:N0}");
                if (modifiedOrHasCutouts)
                {
                    if (GUILayout.Button("Export modified PLY"))
                        ExportPlyFile(gs);
                    if (asset.m_PosFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                        asset.m_ScaleFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                        !GraphicsFormatUtility.IsFloatFormat(asset.m_ColorFormat) ||
                        asset.m_SHFormat > GaussianSplatAsset.SHFormat.Float16)
                    {
                        EditorGUILayout.HelpBox(
                            "It is recommended to use High or VeryHigh quality preset for editing splats, lower levels are lossy",
                            MessageType.Warning);
                    }
                }

                if (hasCutouts)
                {
                    if (s_EditStatsUpdateCounter > 10)
                    {
                        gs.UpdateEditCountsAndBounds();
                        s_EditStatsUpdateCounter = 0;
                    }
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
            Bounds bounds = default;
            bounds.SetMinMax(gs.asset.m_BoundsMin, gs.asset.m_BoundsMax);
            if (gs.editSelectedSplats > 0)
            {
                bounds = gs.editSelectedBounds;
            }
            bounds.extents *= 0.7f;
            return TransformBounds(gs.transform, bounds);
        }

        public static Bounds TransformBounds(Transform tr, Bounds bounds )
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

        static unsafe void ExportPlyFile(GaussianSplatRenderer gs)
        {
            var path = EditorUtility.SaveFilePanel(
                "Export Gaussian Splat PLY file", "", $"{gs.asset.name}-edit.ply", "ply");
            if (string.IsNullOrWhiteSpace(path))
                return;
            int kSplatSize = UnsafeUtility.SizeOf<GaussianSplatAssetCreator.InputSplatData>();
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gs.asset.m_SplatCount, kSplatSize);
            if (!gs.EditExportData(gpuData))
                return;

            GaussianSplatAssetCreator.InputSplatData[] data = new GaussianSplatAssetCreator.InputSplatData[gpuData.count];
            gpuData.GetData(data);

            var gpuDeleted = gs.gpuSplatDeletedBuffer;
            uint[] deleted = new uint[gpuDeleted.count];
            gpuDeleted.GetData(deleted);

            // count non-deleted splats
            int aliveCount = 0;
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                    ++aliveCount;
            }

            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            // note: this is a long string! but we don't use multiline literal because we want guaranteed LF line ending
            var header = $"ply\nformat binary_little_endian 1.0\nelement vertex {aliveCount}\nproperty float x\nproperty float y\nproperty float z\nproperty float nx\nproperty float ny\nproperty float nz\nproperty float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2\nproperty float f_rest_0\nproperty float f_rest_1\nproperty float f_rest_2\nproperty float f_rest_3\nproperty float f_rest_4\nproperty float f_rest_5\nproperty float f_rest_6\nproperty float f_rest_7\nproperty float f_rest_8\nproperty float f_rest_9\nproperty float f_rest_10\nproperty float f_rest_11\nproperty float f_rest_12\nproperty float f_rest_13\nproperty float f_rest_14\nproperty float f_rest_15\nproperty float f_rest_16\nproperty float f_rest_17\nproperty float f_rest_18\nproperty float f_rest_19\nproperty float f_rest_20\nproperty float f_rest_21\nproperty float f_rest_22\nproperty float f_rest_23\nproperty float f_rest_24\nproperty float f_rest_25\nproperty float f_rest_26\nproperty float f_rest_27\nproperty float f_rest_28\nproperty float f_rest_29\nproperty float f_rest_30\nproperty float f_rest_31\nproperty float f_rest_32\nproperty float f_rest_33\nproperty float f_rest_34\nproperty float f_rest_35\nproperty float f_rest_36\nproperty float f_rest_37\nproperty float f_rest_38\nproperty float f_rest_39\nproperty float f_rest_40\nproperty float f_rest_41\nproperty float f_rest_42\nproperty float f_rest_43\nproperty float f_rest_44\nproperty float opacity\nproperty float scale_0\nproperty float scale_1\nproperty float scale_2\nproperty float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\nend_header\n";
            fs.Write(Encoding.UTF8.GetBytes(header));
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                {
                    var splat = data[i];
                    byte* ptr = (byte*)&splat;
                    fs.Write(new ReadOnlySpan<byte>(ptr, kSplatSize));
                }
            }
        }
    }

    [EditorTool("GaussianSplats Tool", typeof(GaussianSplatRenderer))]
    class GaussianSplatsTool : EditorTool
    {
        Vector2 m_MouseStartDragPos;

        public override GUIContent toolbarIcon => EditorGUIUtility.TrIconContent("EditCollider", "Edit Gaussian Splats");

        public override void OnWillBeDeactivated()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs)
                return;
            gs.EditDeselectAll();
        }

        static void HandleKeyboardCommands(Event evt, GaussianSplatRenderer gs)
        {
            if (evt.type != EventType.ValidateCommand && evt.type != EventType.ExecuteCommand)
                return;
            bool execute = evt.type == EventType.ExecuteCommand;
            switch (evt.commandName)
            {
                // ugh, EventCommandNames string constants is internal :(
                case "SoftDelete":
                case "Delete":
                    if (execute)
                    {
                        gs.EditDeleteSelected();
                        GaussianSplatRendererEditor.RepaintAll();
                    }
                    evt.Use();
                    break;
                case "SelectAll":
                    if (execute)
                    {
                        gs.EditSelectAll();
                        GaussianSplatRendererEditor.RepaintAll();
                    }
                    evt.Use();
                    break;
                case "DeselectAll":
                    if (execute)
                    {
                        gs.EditDeselectAll();
                        GaussianSplatRendererEditor.RepaintAll();
                    }
                    evt.Use();
                    break;
                case "InvertSelection":
                    if (execute)
                    {
                        gs.EditInvertSelection();
                        GaussianSplatRendererEditor.RepaintAll();
                    }
                    evt.Use();
                    break;
            }
        }

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView sceneView))
                return;
            var gs = target as GaussianSplatRenderer;
            if (!gs)
                return;

            GaussianSplatRendererEditor.BumpGUICounter();

            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event evt = Event.current;
            HandleKeyboardCommands(evt, gs);
            var evtType = evt.GetTypeForControl(id);
            switch (evtType)
            {
                case EventType.Layout:
                    // make this be the default tool, so that we get focus when user clicks on nothing else
                    HandleUtility.AddDefaultControl(id);
                    break;
                case EventType.MouseDown:
                    if (HandleUtility.nearestControl == id && evt.button == 0 && !evt.alt) // ignore Alt to allow orbiting scene view
                    {
                        // shift/command adds to selection
                        if (!evt.shift && !EditorGUI.actionKey)
                            gs.EditDeselectAll();

                        // record selection state at start
                        gs.EditStoreInitialSelection();
                        GaussianSplatRendererEditor.RepaintAll();

                        GUIUtility.hotControl = id;
                        m_MouseStartDragPos = evt.mousePosition;
                        evt.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id && evt.button == 0)
                    {
                        Rect rect = FromToRect(m_MouseStartDragPos, evt.mousePosition);
                        Vector2 rectMin = HandleUtility.GUIPointToScreenPixelCoordinate(rect.min);
                        Vector2 rectMax = HandleUtility.GUIPointToScreenPixelCoordinate(rect.max);
                        gs.EditUpdateSelection(rectMin, rectMax, sceneView.camera);
                        GaussianSplatRendererEditor.RepaintAll();
                        evt.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id && evt.button == 0)
                    {
                        m_MouseStartDragPos = Vector2.zero;
                        GUIUtility.hotControl = 0;
                        evt.Use();
                    }
                    break;
                case EventType.Repaint:
                    // draw cutout gizmos
                    Handles.color = new Color(1,0,1,0.7f);
                    var prevMatrix = Handles.matrix;
                    foreach (var cutout in gs.m_Cutouts)
                    {
                        if (!cutout)
                            continue;
                        Handles.matrix = cutout.transform.localToWorldMatrix;
                        if (cutout.m_Type == GaussianCutout.Type.Ellipsoid)
                        {
                            Handles.DrawWireDisc(Vector3.zero, Vector3.up, 1.0f);
                            Handles.DrawWireDisc(Vector3.zero, Vector3.right, 1.0f);
                            Handles.DrawWireDisc(Vector3.zero, Vector3.forward, 1.0f);
                        }
                        if (cutout.m_Type == GaussianCutout.Type.Box)
                            Handles.DrawWireCube(Vector3.zero, Vector3.one * 2);
                    }

                    Handles.matrix = prevMatrix;
                    // draw selection bounding box
                    if (gs.editSelectedSplats > 0)
                    {
                        var selBounds = GaussianSplatRendererEditor.TransformBounds(gs.transform, gs.editSelectedBounds);
                        Handles.DrawWireCube(selBounds.center, selBounds.size);
                    }
                    // draw drag rectangle
                    if (GUIUtility.hotControl == id && evt.mousePosition != m_MouseStartDragPos)
                    {
                        GUIStyle style = "SelectionRect";
                        Handles.BeginGUI();
                        style.Draw(FromToRect(m_MouseStartDragPos, evt.mousePosition), false, false, false, false);
                        Handles.EndGUI();
                    }
                    break;
            }
        }

        // build a rect that always has a positive size
        static Rect FromToRect(Vector2 from, Vector2 to)
        {
            if (from.x > to.x)
                (from.x, to.x) = (to.x, from.x);
            if (from.y > to.y)
                (from.y, to.y) = (to.y, from.y);
            return new Rect(from.x, from.y, to.x - from.x, to.y - from.y);
        }
    }
}