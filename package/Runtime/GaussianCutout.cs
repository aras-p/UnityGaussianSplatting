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
            Box,
            Cone
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
                if (self.m_Type == Type.Cone)
                {
                    float sy = Mathf.Abs(tr.lossyScale.y);
                    if (sy > 0.0001f)
                    {
                        // Scale X and Z by 1/sy to decouple FOV from Length (Y)
                        float invSy = 1.0f / sy;
                        Matrix4x4 coneScale = Matrix4x4.Scale(new Vector3(invSy, 1.0f, invSy));
                        sd.matrix = coneScale * sd.matrix;
                    }
                }
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
            if (m_Type == Type.Cone)
            {
                float h = 1.0f;
                float sy = Mathf.Abs(transform.lossyScale.y);
                float r = sy;
                Vector3 apex = Vector3.zero;

                // Draw base circle
                int segments = 32;
                Vector3 prevPt = new Vector3(Mathf.Cos(0) * r, h, Mathf.Sin(0) * r);
                for (int i = 1; i <= segments; ++i)
                {
                    float ang = (i / (float)segments) * Mathf.PI * 2;
                    Vector3 nextPt = new Vector3(Mathf.Cos(ang) * r, h, Mathf.Sin(ang) * r);
                    Gizmos.DrawLine(prevPt, nextPt);
                    prevPt = nextPt;
                }
                // Draw lines from apex to base
                Gizmos.DrawLine(apex, new Vector3(r, h, 0));
                Gizmos.DrawLine(apex, new Vector3(-r, h, 0));
                Gizmos.DrawLine(apex, new Vector3(0, h, r));
                Gizmos.DrawLine(apex, new Vector3(0, h, -r));
            }
        }
#endif // #if UNITY_EDITOR
    }
}
