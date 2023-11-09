// SPDX-License-Identifier: MIT

using System;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [EditorToolContext("GaussianSplats", typeof(GaussianSplatRenderer)), Icon(k_IconPath)]
    class GaussianToolContext : EditorToolContext
    {
        const string k_IconPath = "Packages/org.nesnausk.gaussian-splatting/Editor/Icons/GaussianContext.png";

        Vector2 m_MouseStartDragPos;

        protected override Type GetEditorToolType(Tool tool)
        {
            if (tool == Tool.Move)
                return typeof(GaussianMoveTool);
            //if (tool == Tool.Rotate)
            //    return typeof(GaussianRotateTool); // not correctly working yet
            //if (tool == Tool.Scale)
            //    return typeof(GaussianScaleTool); // not working correctly yet when the GS itself has scale
            return null;
        }

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

        static bool IsViewToolActive()
        {
            return Tools.viewToolActive || Tools.current == Tool.View || (Event.current != null && Event.current.alt);
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
                    if (IsViewToolActive())
                        break;
                    if (HandleUtility.nearestControl == id && evt.button == 0)
                    {
                        // shift/command adds to selection, ctrl removes from selection: if none of these
                        // are present, start a new selection
                        if (!evt.shift && !EditorGUI.actionKey && !evt.control)
                            gs.EditDeselectAll();

                        // record selection state at start
                        gs.EditStoreSelectionMouseDown();
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
                        gs.EditUpdateSelection(rectMin, rectMax, sceneView.camera, evt.control);
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