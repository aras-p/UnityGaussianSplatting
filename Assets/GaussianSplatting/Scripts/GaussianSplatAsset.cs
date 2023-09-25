using System;
using UnityEngine;

public class GaussianSplatAsset : ScriptableObject
{
    public const int kChunkSize = 256;

    [HideInInspector] public int m_SplatCount;
    [HideInInspector] public Vector3 m_BoundsMin;
    [HideInInspector] public Vector3 m_BoundsMax;
    [HideInInspector] public Hash128 m_DataHash;

    public enum VectorFormat
    {
        Norm16, // 6 bytes: 16.16.16
        Norm11, // 4 bytes: 11.10.11
        Norm6   // 2 bytes: 6.5.5
    }

    public static int GetVectorSize(VectorFormat fmt)
    {
        return fmt switch
        {
            VectorFormat.Norm16 => 6,
            VectorFormat.Norm11 => 4,
            VectorFormat.Norm6 => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
        };
    }

    public enum SHFormat
    {
        Full,
        Cluster64k,
        Cluster16k,
        Cluster4k,
        Cluster1k
    }

    public static int GetOtherSize(VectorFormat scaleFormat)
    {
        return
            4 + // rotation
            GetVectorSize(scaleFormat) +
            2; // sh index
    }

    public static int GetSHCount(SHFormat fmt, int splatCount)
    {
        return fmt switch
        {
            SHFormat.Full => splatCount,
            SHFormat.Cluster64k => 64 * 1024,
            SHFormat.Cluster16k => 16 * 1024,
            SHFormat.Cluster4k => 4 * 1024,
            SHFormat.Cluster1k => 1 * 1024,
            _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
        };
    }

    [HideInInspector] public VectorFormat m_PosFormat = VectorFormat.Norm11;
    [HideInInspector] public VectorFormat m_ScaleFormat = VectorFormat.Norm11;
    [HideInInspector] public SHFormat m_SHFormat = SHFormat.Full;

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
