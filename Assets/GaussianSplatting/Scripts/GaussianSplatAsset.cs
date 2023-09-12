using System;
using UnityEngine;

public class GaussianSplatAsset : ScriptableObject
{
    public const int kChunkSize = 256;

    [HideInInspector] public int m_SplatCount;
    [HideInInspector] public Vector3 m_BoundsMin;
    [HideInInspector] public Vector3 m_BoundsMax;

    public enum TexType
    {
        Pos = 0,
        Rot,
        Scl,
        Col,
        SH1,
        SH2,
        SH3,
        SH4,
        SH5,
        SH6,
        SH7,
        SH8,
        SH9,
        SHA,
        SHB,
        SHC,
        SHD,
        SHE,
        SHF,
        TypeCount
    }

    [NonReorderable]
    public Texture2D[] m_Tex = new Texture2D[(int) TexType.TypeCount];

    public Texture2D GetTex(TexType idx) => m_Tex[(int)idx];

    [HideInInspector] public ChunkInfo[] m_Chunks;
    [HideInInspector] public CameraInfo[] m_Cameras;

    [Serializable]
    public struct BoundsInfo
    {
        public Vector3 pos;
        public Vector3 scl;
        public Vector4 col;
        public Vector3 sh1;
        public Vector3 sh2;
        public Vector3 sh3;
        public Vector3 sh4;
        public Vector3 sh5;
        public Vector3 sh6;
        public Vector3 sh7;
        public Vector3 sh8;
        public Vector3 sh9;
        public Vector3 shA;
        public Vector3 shB;
        public Vector3 shC;
        public Vector3 shD;
        public Vector3 shE;
        public Vector3 shF;
    }

    [Serializable]
    public struct ChunkInfo
    {
        public BoundsInfo boundsMin;
        public BoundsInfo boundsInvSize;
    }

    [Serializable]
    public struct CameraInfo
    {
        public Vector3 pos;
        public Vector3 axisX, axisY, axisZ;
        public float fov;
    }
}
