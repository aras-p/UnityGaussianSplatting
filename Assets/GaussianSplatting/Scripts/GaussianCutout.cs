// SPDX-License-Identifier: MIT
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

    public void OnDrawGizmos()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = Color.magenta;
        if (m_Type == Type.Ellipsoid)
        {
            Gizmos.DrawWireSphere(Vector3.zero, 1.0f);
        }
        if (m_Type == Type.Box)
        {
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2);
        }
    }
}
