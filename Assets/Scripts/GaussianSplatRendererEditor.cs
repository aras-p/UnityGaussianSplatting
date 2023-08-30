using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GaussianSplatRenderer))]
public class GaussianSplatRendererEditor : Editor 
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        GUILayout.Label("TODO");
    }
}
