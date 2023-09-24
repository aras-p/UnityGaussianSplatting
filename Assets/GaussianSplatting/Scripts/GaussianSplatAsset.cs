using System;
using UnityEngine;

public class GaussianSplatAsset : ScriptableObject
{
    public const int kChunkSize = 256;

    [HideInInspector] public int m_SplatCount;
    [HideInInspector] public Vector3 m_BoundsMin;
    [HideInInspector] public Vector3 m_BoundsMax;
    [HideInInspector] public Hash128 m_DataHash;

    public enum PosFormat
    {
        Norm16,
        Norm11,
        Norm6
    }

    public static int GetPosSize(PosFormat fmt)
    {
        return fmt switch
        {
            PosFormat.Norm16 => 6,
            PosFormat.Norm11 => 4,
            PosFormat.Norm6 => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
        };
    }

    public enum OtherFormat
    {
        Default,
    }

    public static int GetOtherSize(OtherFormat fmt)
    {
        return 4 + 6 + 2; // rot + scale + SH index
    }

    [HideInInspector] public PosFormat m_PosFormat = PosFormat.Norm11;
    [HideInInspector] public OtherFormat m_OtherFormat = OtherFormat.Default;

    [HideInInspector] public TextAsset m_PosData;
    [HideInInspector] public Texture2D m_ColorData;
    [HideInInspector] public TextAsset m_OtherData;
    [HideInInspector] public TextAsset m_SHData;

    [HideInInspector] public ChunkInfo[] m_Chunks;
    [HideInInspector] public CameraInfo[] m_Cameras;

    [Serializable]
    public struct BoundsInfo
    {
        public Vector4 col;
        public Vector3 pos;
        public Vector3 scl;
    }

    [Serializable]
    public struct ChunkInfo
    {
        public BoundsInfo boundsMin;
        public BoundsInfo boundsMax;
    }

    [Serializable]
    public struct CameraInfo
    {
        public Vector3 pos;
        public Vector3 axisX, axisY, axisZ;
        public float fov;
    }
}
