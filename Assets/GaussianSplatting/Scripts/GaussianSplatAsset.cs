using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public class GaussianSplatAsset : ScriptableObject
{
    public const int kCurrentVersion = 20230930;
    public const int kChunkSize = 256;
    public const int kTextureWidth = 2048; //@TODO: bump to 4k?

    [HideInInspector] public int m_FormatVersion;
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
        Float16,
        Norm11,
        Norm6,
        Cluster64k,
        Cluster32k,
        Cluster16k,
        Cluster8k,
        Cluster4k,
    }

    public struct SHTableItemFloat16
    {
        public half3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        public half3 shPadding; // pad to multiple of 16 bytes
    }
    public struct SHTableItemNorm11
    {
        public uint sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
    }
    public struct SHTableItemNorm6
    {
        public ushort sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        public ushort shPadding; // pad to multiple of 4 bytes
    }

    public static int GetOtherSizeNoSHIndex(VectorFormat scaleFormat)
    {
        return 4 + GetVectorSize(scaleFormat);
    }

    public static int GetSHCount(SHFormat fmt, int splatCount)
    {
        return fmt switch
        {
            SHFormat.Float16 => splatCount,
            SHFormat.Norm11 => splatCount,
            SHFormat.Norm6 => splatCount,
            SHFormat.Cluster64k => 64 * 1024,
            SHFormat.Cluster32k => 32 * 1024,
            SHFormat.Cluster16k => 16 * 1024,
            SHFormat.Cluster8k => 8 * 1024,
            SHFormat.Cluster4k => 4 * 1024,
            _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
        };
    }

    public static (int,int) CalcTextureSize(int splatCount)
    {
        int width = kTextureWidth;
        int height = math.max(1, (splatCount + width - 1) / width);
        // our swizzle tiles are 16x16, so make texture multiple of that height
        int blockHeight = 16;
        height = (height + blockHeight - 1) / blockHeight * blockHeight;
        return (width, height);
    }

    public static long CalcPosDataSize(int splatCount, VectorFormat formatPos)
    {
        return splatCount * GetVectorSize(formatPos);
    }
    public static long CalcOtherDataSize(int splatCount, VectorFormat formatScale)
    {
        return splatCount * GetOtherSizeNoSHIndex(formatScale);
    }
    public static long CalcColorDataSize(int splatCount, GraphicsFormat formatColor)
    {
        var (width, height) = CalcTextureSize(splatCount);
        return GraphicsFormatUtility.ComputeMipmapSize(width, height, formatColor);
    }
    public static long CalcSHDataSize(int splatCount, SHFormat formatSh)
    {
        int shCount = GetSHCount(formatSh, splatCount);
        return formatSh switch
        {
            SHFormat.Float16 => shCount * UnsafeUtility.SizeOf<SHTableItemFloat16>(),
            SHFormat.Norm11 => shCount * UnsafeUtility.SizeOf<SHTableItemNorm11>(),
            SHFormat.Norm6 => shCount * UnsafeUtility.SizeOf<SHTableItemNorm6>(),
            _ => shCount * UnsafeUtility.SizeOf<SHTableItemFloat16>() + splatCount * 2
        };
    }
    public static long CalcChunkDataSize(int splatCount)
    {
        int chunkCount = (splatCount + kChunkSize - 1) / kChunkSize;
        return chunkCount * UnsafeUtility.SizeOf<ChunkInfo>();
    }

    [HideInInspector] public VectorFormat m_PosFormat = VectorFormat.Norm11;
    [HideInInspector] public VectorFormat m_ScaleFormat = VectorFormat.Norm11;
    [HideInInspector] public SHFormat m_SHFormat = SHFormat.Norm11;

    [HideInInspector] public TextAsset m_PosData;
    [HideInInspector] public Texture2D m_ColorData;
    [HideInInspector] public TextAsset m_OtherData;
    [HideInInspector] public TextAsset m_SHData;
    [HideInInspector] public TextAsset m_ChunkData;

    [HideInInspector] public CameraInfo[] m_Cameras;

    public struct ChunkInfo
    {
        public uint colR, colG, colB, colA;
        public float2 posX, posY, posZ;
        public uint sclX, sclY, sclZ;
        public uint shR, shG, shB;
    }

    [Serializable]
    public struct CameraInfo
    {
        public Vector3 pos;
        public Vector3 axisX, axisY, axisZ;
        public float fov;
    }
}
