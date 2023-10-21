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

            var splatCount = gs.splatCount;
            {
                using var _ = new EditorGUI.DisabledScope(true);
                EditorGUILayout.IntField("Splats", splatCount);
                var prevBackColor = GUI.backgroundColor;
                if (gs.formatVersion != GaussianSplatAsset.kCurrentVersion)
                    GUI.backgroundColor *= Color.red;
                EditorGUILayout.IntField("Version", gs.formatVersion);
                GUI.backgroundColor = prevBackColor;

                long sizePos = GaussianSplatAsset.CalcPosDataSize(gs.splatCount, gs.posFormat);
                long sizeOther = GaussianSplatAsset.CalcOtherDataSize(gs.splatCount, gs.scaleFormat);
                long sizeCol = GaussianSplatAsset.CalcColorDataSize(gs.splatCount, gs.colorFormat);
                long sizeSH = GaussianSplatAsset.CalcSHDataSize(gs.splatCount, gs.shFormat);
                long sizeChunk = gs.chunkData != null ? GaussianSplatAsset.CalcChunkDataSize(gs.splatCount) : 0;

                EditorGUILayout.TextField("Memory", EditorUtility.FormatBytes(sizePos + sizeOther + sizeSH + sizeCol + sizeChunk));
                EditorGUI.indentLevel++;
                EditorGUILayout.TextField("Positions", $"{EditorUtility.FormatBytes(sizePos)}  ({gs.posFormat})");
                EditorGUILayout.TextField("Other", $"{EditorUtility.FormatBytes(sizeOther)}  ({gs.scaleFormat})");
                EditorGUILayout.TextField("Base color", $"{EditorUtility.FormatBytes(sizeCol)}  ({gs.colorFormat})");
                EditorGUILayout.TextField("SHs", $"{EditorUtility.FormatBytes(sizeSH)}  ({gs.shFormat})");
                EditorGUILayout.TextField("Chunks", $"{EditorUtility.FormatBytes(sizeChunk)}  ({UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()} B/chunk)");
                EditorGUI.indentLevel--;

                EditorGUILayout.Vector3Field("Bounds Min", gs.boundsMin);
                EditorGUILayout.Vector3Field("Bounds Max", gs.boundsMax);

                EditorGUILayout.TextField("Data Hash", gs.dataHash.ToString());
            }
        }
    }
}
