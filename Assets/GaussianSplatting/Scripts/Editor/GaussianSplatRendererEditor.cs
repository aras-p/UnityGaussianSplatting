using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GaussianSplatRenderer))]
public class GaussianSplatRendererEditor : Editor
{
    int m_CameraIndex = 0;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var gs = target as GaussianSplatRenderer;
        if (!gs)
            return;
        if (!gs.HasValidAsset)
        {
            EditorGUILayout.HelpBox("Gaussian Splat asset is null or empty", MessageType.Error);
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

        EditorGUILayout.Space();
        GUILayout.Label("Stats / Controls", EditorStyles.boldLabel);
        {
            using var _ = new EditorGUI.DisabledScope(true);
            EditorGUILayout.IntField("Splat Count", asset.m_SplatCount);
        }
        var cameras = asset.m_Cameras;
        if (cameras != null && cameras.Length != 0)
        {
            var camIndex = EditorGUILayout.IntSlider("Camera", m_CameraIndex, 0, cameras.Length - 1);
            camIndex = math.clamp(camIndex, 0, cameras.Length - 1);
            if (camIndex != m_CameraIndex)
            {
                m_CameraIndex = camIndex;
                gs.ActivateCamera(camIndex);
            }
        }
    }
}
