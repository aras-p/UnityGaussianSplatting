using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[CustomEditor(typeof(GaussianSplatAsset))]
public class GaussianSplatAssetEditor : Editor
{
    static long GetTextureSize(Texture2D tex)
    {
        if (tex == null)
            return 0;
        return GraphicsFormatUtility.ComputeMipmapSize(tex.width, tex.height, tex.graphicsFormat);
    }

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

            long sizePos = gs.m_PosData != null ? gs.m_PosData.dataSize : 0;
            long sizeOther = gs.m_OtherData != null ? gs.m_OtherData.dataSize : 0;
            long sizeSH = gs.m_SHData != null ? gs.m_SHData.dataSize : 0;
            long sizeCol = GetTextureSize(gs.m_ColorData);
            long sizeChk = gs.m_Chunks.Length * UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>();
            EditorGUILayout.TextField("Memory", EditorUtility.FormatBytes(sizePos + sizeOther + sizeSH + sizeCol + sizeChk));
            EditorGUI.indentLevel++;
            EditorGUILayout.TextField("Positions", $"{EditorUtility.FormatBytes(sizePos)}  {gs.m_PosFormat}");
            EditorGUILayout.TextField("Other", $"{EditorUtility.FormatBytes(sizeOther)}  {gs.m_OtherFormat}");
            EditorGUILayout.TextField("Base color", $"{EditorUtility.FormatBytes(sizeCol)}  {(gs.m_ColorData != null ? gs.m_ColorData.graphicsFormat : "")}");
            EditorGUILayout.TextField("SHs", $"{EditorUtility.FormatBytes(sizeSH)}  ({gs.m_SHFormat})");
            EditorGUILayout.TextField("Chunks", $"{EditorUtility.FormatBytes(sizeChk)}  ({UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()} B/chunk)");
            EditorGUI.indentLevel--;

            EditorGUILayout.Vector3Field("Bounds Min", gs.m_BoundsMin);
            EditorGUILayout.Vector3Field("Bounds Max", gs.m_BoundsMax);
            
            EditorGUILayout.TextField("Data Hash", gs.m_DataHash.ToString());
        }
    }
}
