// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [EditorTool("Gaussian Move Tool", typeof(GaussianSplatRenderer), typeof(GaussianToolContext))]
    class GaussianMoveTool : GaussianTool
    {
        public override void OnToolGUI(EditorWindow window)
        {
            var gs = GetRenderer();
            if (!gs || !CanBeEdited() || !HasSelection())
                return;
            var tr = gs.transform;

            EditorGUI.BeginChangeCheck();
            var selCenterLocal = GetSelectionCenterLocal();
            var selCenterWorld = tr.TransformPoint(selCenterLocal);
            var newPosWorld = Handles.DoPositionHandle(selCenterWorld, Tools.handleRotation);
            if (EditorGUI.EndChangeCheck())
            {
                var newPosLocal = tr.InverseTransformPoint(newPosWorld);
                var wasModified = gs.editModified;
                gs.EditTranslateSelection(newPosLocal - selCenterLocal);
                if (!wasModified)
                    GaussianSplatRendererEditor.RepaintAll();
                Event.current.Use();
            }
        }
    }
}
