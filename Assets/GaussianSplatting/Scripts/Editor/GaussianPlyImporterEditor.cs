using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

[CustomEditor(typeof(GaussianPlyImporter))]
public class GaussianPlyImporterEditor : AssetImporterEditor
{
    int m_FileVertexCount;
    long m_FileSize;

    SerializedProperty m_PropImportCameras;
    SerializedProperty m_PropQuality;
    SerializedProperty m_PropFormatPos;
    SerializedProperty m_PropFormatScale;
    SerializedProperty m_PropFormatSH;
    SerializedProperty m_PropFormatColor;

    public override void OnEnable()
    {
        base.OnEnable();

        m_FileVertexCount = 0;
        m_FileSize = 0;
        var imp = target as GaussianPlyImporter;
        if (imp)
        {
            PLYFileReader.ReadFileHeader(imp.assetPath, out m_FileVertexCount, out _, out _);
            m_FileSize = new FileInfo(imp.assetPath).Length;
        }

        m_PropImportCameras = serializedObject.FindProperty("m_ImportCameras");
        m_PropQuality = serializedObject.FindProperty("m_Quality");
        m_PropFormatPos = serializedObject.FindProperty("m_FormatPos");
        m_PropFormatScale = serializedObject.FindProperty("m_FormatScale");
        m_PropFormatSH = serializedObject.FindProperty("m_FormatSH");
        m_PropFormatColor = serializedObject.FindProperty("m_FormatColor");

        ApplyQualityLevel();
    }

    void ApplyQualityLevel()
    {
        GaussianPlyImporter.DataQuality quality = (GaussianPlyImporter.DataQuality) m_PropQuality.intValue;
        switch (quality)
        {
            case GaussianPlyImporter.DataQuality.Custom:
                break;
            case GaussianPlyImporter.DataQuality.VeryLow: // 18.4x smaller, 32.27 PSNR (was: 20.7x smaller, 24.07 PSNR)
                m_PropFormatPos.intValue = (int)GaussianSplatAsset.VectorFormat.Norm11;
                m_PropFormatScale.intValue = (int)GaussianSplatAsset.VectorFormat.Norm6;
                m_PropFormatColor.intValue = (int)GaussianPlyImporter.ColorFormat.BC7;
                m_PropFormatSH.intValue = (int)GaussianSplatAsset.SHFormat.Cluster4k;
                break;
            case GaussianPlyImporter.DataQuality.Low: // 14.9x smaller, 35.17 PSNR (was: 13.1x smaller, 34.76 PSNR)
                m_PropFormatPos.intValue = (int)GaussianSplatAsset.VectorFormat.Norm11;
                m_PropFormatScale.intValue = (int)GaussianSplatAsset.VectorFormat.Norm6;
                m_PropFormatColor.intValue = (int)GaussianPlyImporter.ColorFormat.Norm8x4;
                m_PropFormatSH.intValue = (int)GaussianSplatAsset.SHFormat.Cluster16k;
                break;
            case GaussianPlyImporter.DataQuality.Medium: // 5.1x smaller, 47.46 PSNR (was: 5.3x smaller, 47.51 PSNR)
                m_PropFormatPos.intValue = (int)GaussianSplatAsset.VectorFormat.Norm11;
                m_PropFormatScale.intValue = (int)GaussianSplatAsset.VectorFormat.Norm11;
                m_PropFormatColor.intValue = (int)GaussianPlyImporter.ColorFormat.Norm8x4;
                m_PropFormatSH.intValue = (int)GaussianSplatAsset.SHFormat.Norm6;
                break;
            case GaussianPlyImporter.DataQuality.High: // 2.9x smaller, 57.77 PSNR (was: 2.9x smaller, 54.87 PSNR)
                m_PropFormatPos.intValue = (int)GaussianSplatAsset.VectorFormat.Norm16;
                m_PropFormatScale.intValue = (int)GaussianSplatAsset.VectorFormat.Norm16;
                m_PropFormatColor.intValue = (int)GaussianPlyImporter.ColorFormat.Float16x4;
                m_PropFormatSH.intValue = (int)GaussianSplatAsset.SHFormat.Norm11;
                break;
            case GaussianPlyImporter.DataQuality.VeryHigh: // 2.1x smaller (was: 0.8x smaller)
                m_PropFormatPos.intValue = (int)GaussianSplatAsset.VectorFormat.Norm16;
                m_PropFormatScale.intValue = (int)GaussianSplatAsset.VectorFormat.Norm16;
                m_PropFormatColor.intValue = (int)GaussianPlyImporter.ColorFormat.Float16x4;
                m_PropFormatSH.intValue = (int)GaussianSplatAsset.SHFormat.Float16;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        if (m_FileVertexCount > 0)
            EditorGUILayout.LabelField("Input File Size", $"{EditorUtility.FormatBytes(m_FileSize)} - {m_FileVertexCount:N0} splats");

        EditorGUILayout.PropertyField(m_PropImportCameras);
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(m_PropQuality);
        if (EditorGUI.EndChangeCheck())
        {
            ApplyQualityLevel();
        }

        long sizePos = 0, sizeOther = 0, sizeCol = 0, sizeSHs = 0, totalSize = 0;
        if (m_FileVertexCount > 0)
        {
            sizePos = GaussianSplatAsset.CalcPosDataSize(m_FileVertexCount, (GaussianSplatAsset.VectorFormat)m_PropFormatPos.intValue);
            sizeOther = GaussianSplatAsset.CalcOtherDataSize(m_FileVertexCount, (GaussianSplatAsset.VectorFormat)m_PropFormatScale.intValue);
            sizeCol = GaussianSplatAsset.CalcColorDataSize(m_FileVertexCount, GaussianPlyImporter.ColorFormatToGraphics((GaussianPlyImporter.ColorFormat)m_PropFormatColor.intValue));
            sizeSHs = GaussianSplatAsset.CalcSHDataSize(m_FileVertexCount, (GaussianSplatAsset.SHFormat)m_PropFormatSH.intValue);
            long sizeChunk = GaussianSplatAsset.CalcChunkDataSize(m_FileVertexCount);
            totalSize = sizePos + sizeOther + sizeCol + sizeSHs + sizeChunk;
        }

        const float kSizeColWidth = 70;
        EditorGUI.BeginDisabledGroup(m_PropQuality.intValue != (int)GaussianPlyImporter.DataQuality.Custom);
        EditorGUI.indentLevel++;
        GUILayout.BeginHorizontal();
        EditorGUILayout.PropertyField(m_PropFormatPos);
        GUILayout.Label(sizePos > 0 ? EditorUtility.FormatBytes(sizePos) : string.Empty, GUILayout.Width(kSizeColWidth));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        EditorGUILayout.PropertyField(m_PropFormatScale);
        GUILayout.Label(sizeOther > 0 ? EditorUtility.FormatBytes(sizeOther) : string.Empty, GUILayout.Width(kSizeColWidth));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        EditorGUILayout.PropertyField(m_PropFormatColor);
        GUILayout.Label(sizeCol > 0 ? EditorUtility.FormatBytes(sizeCol) : string.Empty, GUILayout.Width(kSizeColWidth));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        EditorGUILayout.PropertyField(m_PropFormatSH);
        GUIContent shGC = new GUIContent();
        shGC.text = sizeSHs > 0 ? EditorUtility.FormatBytes(sizeSHs) : string.Empty;
        if (m_PropFormatSH.intValue >= (int)GaussianSplatAsset.SHFormat.Cluster64k)
        {
            shGC.tooltip = "Note that SH clustering is not fast! (3-10 minutes for 6M splats)";
            shGC.image = EditorGUIUtility.IconContent("console.warnicon.sml").image;
        }
        GUILayout.Label(shGC, GUILayout.Width(kSizeColWidth));
        GUILayout.EndHorizontal();
        EditorGUI.indentLevel--;
        EditorGUI.EndDisabledGroup();
        if (totalSize > 0)
            EditorGUILayout.LabelField("Asset Size", $"{EditorUtility.FormatBytes(totalSize)} - {(double) m_FileSize / totalSize:F1}x smaller");

        serializedObject.ApplyModifiedProperties();

        ApplyRevertGUI();
    }
}
