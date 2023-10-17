// SPDX-License-Identifier: MIT

using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class GaussianCutout : MonoBehaviour
    {
        public enum Type
        {
            Ellipsoid,
            Box
        }

        public Type m_Type = Type.Ellipsoid;
        public bool m_Invert = false;

        public struct ShaderData // match GaussianCutoutShaderData in CS
        {
            public Matrix4x4 matrix;
            public uint typeAndFlags;
        }

        public static ShaderData GetShaderData(GaussianCutout self, Matrix4x4 rendererMatrix)
        {
            ShaderData sd = default;
            if (self && self.isActiveAndEnabled)
            {
                var tr = self.transform;
                sd.matrix = tr.worldToLocalMatrix * rendererMatrix;
                sd.typeAndFlags = ((uint)self.m_Type) | (self.m_Invert ? 0x100u : 0u);
            }
            else
            {
                sd.typeAndFlags = ~0u;
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
}
