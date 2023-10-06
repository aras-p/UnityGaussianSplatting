using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[CustomEditor(typeof(GaussianSplatRenderer))]
public class GaussianSplatRendererEditor : Editor
{
    int m_CameraIndex = 0;

    static HashSet<GaussianSplatRendererEditor> s_AllEditors = new();

    public static void RepaintAll()
    {
        foreach (var e in s_AllEditors)
            e.Repaint();
    }

    public void OnEnable()
    {
        s_AllEditors.Add(this);
    }

    public void OnDisable()
    {
        s_AllEditors.Remove(this);
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var gs = target as GaussianSplatRenderer;
        if (!gs)
            return;
        if (!gs.HasValidAsset)
        {
            var msg = gs.asset != null && gs.asset.m_FormatVersion != GaussianSplatAsset.kCurrentVersion
                ? "Gaussian Splat asset version is not compatible, please recreate the asset"
                : "Gaussian Splat asset is not assigned or is empty";
            EditorGUILayout.HelpBox(msg, MessageType.Error);
            return;
        }
        if (!gs.enabled || !gs.gameObject.activeInHierarchy)
            return;
        if (!gs.HasValidRenderSetup)
        {
            EditorGUILayout.HelpBox("Shader resources are not set up", MessageType.Error);
            return;
        }
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

        if (gs.editModified || gs.editSelectedSplats != 0 || gs.editDeletedSplats != 0)
        {
            EditorGUILayout.Space();
            GUILayout.Label("Editing", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Splats", $"{asset.m_SplatCount:N0}");
            EditorGUILayout.LabelField("Deleted", $"{gs.editDeletedSplats:N0}");
            EditorGUILayout.LabelField("Selected", $"{gs.editSelectedSplats:N0}");
            if (gs.editModified)
            {
                if (GUILayout.Button("Export modified PLY"))
                    ExportPlyFile(gs);
                if (asset.m_PosFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                    asset.m_ScaleFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                    !GraphicsFormatUtility.IsFloatFormat(asset.m_ColorFormat) ||
                    asset.m_SHFormat > GaussianSplatAsset.SHFormat.Float16)
                {
                    EditorGUILayout.HelpBox("It is recommended to use High or VeryHigh quality preset for editing splats, lower levels are lossy", MessageType.Warning);
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
            if ((deleted[wordIdx] & (1u << bitIdx)) == 0)
                ++aliveCount;
        }
        
        using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var header = $@"ply
format binary_little_endian 1.0
element vertex {aliveCount}
property float x
property float y
property float z
property float nx
property float ny
property float nz
property float f_dc_0
property float f_dc_1
property float f_dc_2
property float f_rest_0
property float f_rest_1
property float f_rest_2
property float f_rest_3
property float f_rest_4
property float f_rest_5
property float f_rest_6
property float f_rest_7
property float f_rest_8
property float f_rest_9
property float f_rest_10
property float f_rest_11
property float f_rest_12
property float f_rest_13
property float f_rest_14
property float f_rest_15
property float f_rest_16
property float f_rest_17
property float f_rest_18
property float f_rest_19
property float f_rest_20
property float f_rest_21
property float f_rest_22
property float f_rest_23
property float f_rest_24
property float f_rest_25
property float f_rest_26
property float f_rest_27
property float f_rest_28
property float f_rest_29
property float f_rest_30
property float f_rest_31
property float f_rest_32
property float f_rest_33
property float f_rest_34
property float f_rest_35
property float f_rest_36
property float f_rest_37
property float f_rest_38
property float f_rest_39
property float f_rest_40
property float f_rest_41
property float f_rest_42
property float f_rest_43
property float f_rest_44
property float opacity
property float scale_0
property float scale_1
property float scale_2
property float rot_0
property float rot_1
property float rot_2
property float rot_3
end_header
";
        fs.Write(Encoding.UTF8.GetBytes(header));
        for (int i = 0; i < data.Length; ++i)
        {
            int wordIdx = i >> 5;
            int bitIdx = i & 31;
            if ((deleted[wordIdx] & (1u << bitIdx)) == 0)
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
                if (gs.editSelectedSplats > 0)
                {
                    var selBounds = GaussianSplatRendererEditor.TransformBounds(gs.transform, gs.editSelectedBounds);
                    Handles.color = new Color(1,0,1,0.7f);
                    Handles.DrawWireCube(selBounds.center, selBounds.size);
                }
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

