using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

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
            EditorGUILayout.LabelField("Splats", $"{asset.m_SplatCount/1000.0:F1}k splats");
            EditorGUILayout.LabelField("Deleted", $"{gs.editDeletedSplats/1000.0:F1}k splats");
            EditorGUILayout.LabelField("Selected", $"{gs.editSelectedSplats/1000.0:F1}k splats");
            if (gs.editModified)
            {
                GUILayout.Button("TODO save!");
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

