// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatAsset))]
    public class GaussianSplatAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var gs = target as GaussianSplatAsset;
            if (!gs)
                return;

            EditorGUILayout.Space();

            var splatCount = gs.m_SplatCount;
            {
                using var _ = new EditorGUI.DisabledScope(true);
                EditorGUILayout.IntField("Splats", splatCount);
                var prevBackColor = GUI.backgroundColor;
                if (gs.m_FormatVersion != GaussianSplatAsset.kCurrentVersion)
                    GUI.backgroundColor *= Color.red;
                EditorGUILayout.IntField("Version", gs.m_FormatVersion);
                GUI.backgroundColor = prevBackColor;

                long sizePos = GaussianSplatAsset.CalcPosDataSize(gs.m_SplatCount, gs.m_PosFormat);
                long sizeOther = GaussianSplatAsset.CalcOtherDataSize(gs.m_SplatCount, gs.m_ScaleFormat);
                long sizeCol = GaussianSplatAsset.CalcColorDataSize(gs.m_SplatCount, gs.m_ColorFormat);
                long sizeSH = GaussianSplatAsset.CalcSHDataSize(gs.m_SplatCount, gs.m_SHFormat);
                long sizeChunk = GaussianSplatAsset.CalcChunkDataSize(gs.m_SplatCount);

                EditorGUILayout.TextField("Memory", EditorUtility.FormatBytes(sizePos + sizeOther + sizeSH + sizeCol + sizeChunk));
                EditorGUI.indentLevel++;
                EditorGUILayout.TextField("Positions", $"{EditorUtility.FormatBytes(sizePos)}  ({gs.m_PosFormat})");
                EditorGUILayout.TextField("Other", $"{EditorUtility.FormatBytes(sizeOther)}  ({gs.m_ScaleFormat})");
                EditorGUILayout.TextField("Base color", $"{EditorUtility.FormatBytes(sizeCol)}  ({gs.m_ColorFormat})");
                EditorGUILayout.TextField("SHs", $"{EditorUtility.FormatBytes(sizeSH)}  ({gs.m_SHFormat})");
                EditorGUILayout.TextField("Chunks", $"{EditorUtility.FormatBytes(sizeChunk)}  ({UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()} B/chunk)");
                EditorGUI.indentLevel--;

                EditorGUILayout.Vector3Field("Bounds Min", gs.m_BoundsMin);
                EditorGUILayout.Vector3Field("Bounds Max", gs.m_BoundsMax);

                EditorGUILayout.TextField("Data Hash", gs.m_DataHash.ToString());
            }
        }
    }
}
