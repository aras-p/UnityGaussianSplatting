using UnityEngine;

public class GaussianSplatAsset : ScriptableObject
{
    public int m_SplatCount;
    public Vector3 m_BoundsMin;
    public Vector3 m_BoundsMax;

    public Texture2D m_TexPos;
    public Texture2D m_TexRot;
    public Texture2D m_TexScl;
    public Texture2D m_TexCol;
    public Texture2D m_TexSH1;
    public Texture2D m_TexSH2;
    public Texture2D m_TexSH3;
    public Texture2D m_TexSH4;
    public Texture2D m_TexSH5;
    public Texture2D m_TexSH6;
    public Texture2D m_TexSH7;
    public Texture2D m_TexSH8;
    public Texture2D m_TexSH9;
    public Texture2D m_TexSHA;
    public Texture2D m_TexSHB;
    public Texture2D m_TexSHC;
    public Texture2D m_TexSHD;
    public Texture2D m_TexSHE;
    public Texture2D m_TexSHF;

    public struct BoundsInfo
    {
        public Vector3 pos;
        public Vector3 scale;
        public Vector4 colorOp;
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

    public struct ChunkInfo
    {
        public BoundsInfo boundsMin;
        public BoundsInfo boundsInvSize;
    }

    public ChunkInfo[] m_Chunks;
}
