// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [EditorTool("Gaussian Scale Tool", typeof(GaussianSplatRenderer), typeof(GaussianToolContext))]
    class GaussianScaleTool : GaussianTool
    {
        Vector3 m_CurrentScale = Vector3.one;
        Vector3 m_FrozenSelCenterLocal = Vector3.zero;
        bool m_FreezePivot = false;

        public override void OnActivated()
        {
            m_FreezePivot = false;
        }

        public override void OnToolGUI(EditorWindow window)
        {
            var gs = GetRenderer();
            if (!gs || !CanBeEdited() || !HasSelection())
                return;
            var tr = gs.transform;
            var evt = Event.current;

            var selCenterLocal = GetSelectionCenterLocal();
            if (evt.type == EventType.MouseDown)
            {
                gs.EditStorePosMouseDown();
                m_FrozenSelCenterLocal = selCenterLocal;
                m_FreezePivot = true;
            }
            if (evt.type == EventType.MouseUp)
            {
                m_CurrentScale = Vector3.one;
                m_FreezePivot = false;
            }

            if (m_FreezePivot)
                selCenterLocal = m_FrozenSelCenterLocal;

            EditorGUI.BeginChangeCheck();
            var selCenterWorld = tr.TransformPoint(selCenterLocal);
            m_CurrentScale = Handles.DoScaleHandle(m_CurrentScale, selCenterWorld, Tools.handleRotation, HandleUtility.GetHandleSize(selCenterWorld));
            if (EditorGUI.EndChangeCheck())
            {
                var wasModified = gs.editModified;
                gs.EditScaleSelection(selCenterLocal, m_CurrentScale);
                if (!wasModified)
                    GaussianSplatRendererEditor.RepaintAll();
                evt.Use();
            }
        }
    }
}