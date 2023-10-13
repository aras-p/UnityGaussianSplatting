// SPDX-License-Identifier: MIT
using System.Linq;
using UnityEditor;
using UnityEngine;

public class GaussianCutout : MonoBehaviour
{
    public enum Type
    {
        Ellipsoid,
        Box
    }

    public Type m_Type = Type.Ellipsoid;

    public struct ShaderData // match GaussianCutoutShaderData in CS
    {
        public Matrix4x4 matrix;
        public int type;
    }

    public static ShaderData GetShaderData(GaussianCutout self, Matrix4x4 rendererMatrix)
    {
        ShaderData sd = default;
        if (self != null)
        {
            var tr = self.transform;
            sd.matrix = tr.worldToLocalMatrix * rendererMatrix;
            sd.type = (int)self.m_Type;
        }
        else
        {
            sd.type = -1;
        }
        return sd;
    }

    #if UNITY_EDITOR
    public void OnDrawGizmos()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        var color = Color.magenta;
        color.a = 0.2f;
        if (Selection.Contains(gameObject))
            color.a = 0.9f;
        else
        {
            // mid amount of alpha if a GS object that contains us as a cutout is selected
            var activeGo = Selection.activeGameObject;
            if (activeGo != null)
            {
                var activeSplat = activeGo.GetComponent<GaussianSplatRenderer>();
                if (activeSplat != null)
                {
                    if (activeSplat.m_Cutouts != null && activeSplat.m_Cutouts.Contains(this))
                        color.a = 0.5f;
                }
            }
        }

        Gizmos.color = color;
        if (m_Type == Type.Ellipsoid)
        {
            Gizmos.DrawWireSphere(Vector3.zero, 1.0f);
        }
        if (m_Type == Type.Box)
        {
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2);
        }
    }
    #endif // #if UNITY_EDITOR
}
