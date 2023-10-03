using System;
using System.Collections.Generic;
using System.IO;
using TinyJson;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[ScriptedImporter(GaussianSplatAsset.kCurrentVersion, "ply", AllowCaching = true)]
public class GaussianPlyImporter : ScriptedImporter
{
    const string kProgressTitle = "Importing Gaussian Splat asset";
    const string kCamerasJson = "cameras.json";

    public enum DataQuality
    {
        VeryHigh,
        High,
        Medium,
        Low,
        VeryLow,
        Custom,
    }

    public enum ColorFormat
    {
        Float32x4,
        Float16x4,
        Norm8x4,
        BC7,
    }

    public bool m_ImportCameras = true;
    // ReSharper disable once NotAccessedField.Global - used by inspector UI
    public DataQuality m_Quality = DataQuality.Medium;
    public GaussianSplatAsset.VectorFormat m_FormatPos = GaussianSplatAsset.VectorFormat.Norm11;
    public GaussianSplatAsset.VectorFormat m_FormatScale = GaussianSplatAsset.VectorFormat.Norm11;
    public GaussianSplatAsset.SHFormat m_FormatSH = GaussianSplatAsset.SHFormat.Norm6;
    public ColorFormat m_FormatColor = ColorFormat.Norm8x4;

    public override void OnImportAsset(AssetImportContext ctx)
    {
        try
        {
            var gs = CreateAsset(ctx);
            if (gs == null)
                return;
            ctx.AddObjectToAsset(Path.GetFileNameWithoutExtension(ctx.assetPath), gs);
            ctx.SetMainObject(gs);
            gs.m_PosData.name = gs.name + "_pos";
            gs.m_OtherData.name = gs.name + "_oth";
            gs.m_ColorData.name = gs.name + "_col";
            gs.m_SHData.name = gs.name + "_shs";
            gs.m_ChunkData.name = gs.name + "_chk";
            ctx.AddObjectToAsset(gs.m_PosData.name, gs.m_PosData);
            ctx.AddObjectToAsset(gs.m_OtherData.name, gs.m_OtherData);
            ctx.AddObjectToAsset(gs.m_ColorData.name, gs.m_ColorData);
            ctx.AddObjectToAsset(gs.m_SHData.name, gs.m_SHData);
            ctx.AddObjectToAsset(gs.m_ChunkData.name, gs.m_ChunkData);
        }
        catch (Exception ex)
        {
            ctx.LogImportError($"Error importing '{ctx.assetPath}': {ex.Message}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    unsafe GaussianSplatAsset CreateAsset(AssetImportContext ctx)
    {
        string baseName = Path.GetFileNameWithoutExtension(ctx.assetPath);
        EditorUtility.DisplayProgressBar(kProgressTitle, $"{baseName}: reading input files", 0.0f);
        GaussianSplatAsset.CameraInfo[] cameras = LoadJsonCamerasFile(ctx.assetPath, m_ImportCameras);
        using NativeArray<InputSplatData> inputSplats = LoadPLYSplatFile(ctx);
        if (inputSplats.Length == 0)
        {
            ctx.LogImportError($"PLY file {ctx.assetPath} had no splats");
            return null;
        }

        float3 boundsMin, boundsMax;
        var boundsJob = new CalcBoundsJob
        {
            m_BoundsMin = &boundsMin,
            m_BoundsMax = &boundsMax,
            m_SplatData = inputSplats
        };
        boundsJob.Schedule().Complete();

        EditorUtility.DisplayProgressBar(kProgressTitle, $"{baseName}: Morton reordering", 0.05f);
        ReorderMorton(inputSplats, boundsMin, boundsMax);

        // cluster SHs
        NativeArray<int> splatSHIndices = default;
        NativeArray<GaussianSplatAsset.SHTableItemFloat16> clusteredSHs = default;
        if (m_FormatSH >= GaussianSplatAsset.SHFormat.Cluster64k)
        {
            EditorUtility.DisplayProgressBar(kProgressTitle, $"{baseName}: Cluster SHs", 0.2f);
            ClusterSHs(inputSplats, m_FormatSH, out clusteredSHs, out splatSHIndices);
        }


        GaussianSplatAsset asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        asset.name = baseName;
        asset.m_Cameras = cameras;
        asset.m_BoundsMin = boundsMin;
        asset.m_BoundsMax = boundsMax;

        EditorUtility.DisplayProgressBar(kProgressTitle, $"{baseName}: Creating data objects", 0.7f);
        asset.m_SplatCount = inputSplats.Length;
        asset.m_FormatVersion = GaussianSplatAsset.kCurrentVersion;
        asset.m_PosFormat = m_FormatPos;
        asset.m_ScaleFormat = m_FormatScale;
        asset.m_SHFormat = m_FormatSH;
        asset.m_DataHash = new Hash128((uint)asset.m_SplatCount, (uint)asset.m_FormatVersion, 0, 0);
        LinearizeData(inputSplats);
        asset.m_ChunkData = CreateChunkData(inputSplats, ref asset.m_DataHash, m_FormatSH);
        asset.m_PosData = CreatePositionsData(inputSplats, ref asset.m_DataHash);
        asset.m_OtherData = CreateOtherData(inputSplats, ref asset.m_DataHash, splatSHIndices);
        asset.m_ColorData = CreateColorData(inputSplats, ref asset.m_DataHash, out asset.m_ColorWidth, out asset.m_ColorHeight, out asset.m_ColorFormat);
        asset.m_SHData = CreateSHData(inputSplats, ref asset.m_DataHash, clusteredSHs);

        splatSHIndices.Dispose();
        clusteredSHs.Dispose();

        return asset;
    }

    // input file splat data is expected to be in this format
    public struct InputSplatData
    {
        public Vector3 pos;
        public Vector3 nor;
        public Vector3 dc0;
        public Vector3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }

    unsafe NativeArray<InputSplatData> LoadPLYSplatFile(AssetImportContext ctx)
    {
        NativeArray<InputSplatData> data = default;
        if (!File.Exists(ctx.assetPath))
        {
            ctx.LogImportError($"Did not find {ctx.assetPath} file");
            return data;
        }

        PLYFileReader.ReadFile(ctx.assetPath, out var splatCount, out int vertexStride, out _, out var verticesRawData);
        if (UnsafeUtility.SizeOf<InputSplatData>() != vertexStride)
        {
            ctx.LogImportError($"PLY vertex size mismatch, expected {UnsafeUtility.SizeOf<InputSplatData>()} but file has {vertexStride}");
            return data;
        }

        // reorder SHs
        NativeArray<float> floatData = verticesRawData.Reinterpret<float>(1);
        ReorderSHs(splatCount, (float*)floatData.GetUnsafePtr());

        return verticesRawData.Reinterpret<InputSplatData>(1);
    }

    [BurstCompile]
    static unsafe void ReorderSHs(int splatCount, float* data)
    {
        int splatStride = UnsafeUtility.SizeOf<InputSplatData>() / 4;
        int shStartOffset = 9, shCount = 15;
        float* tmp = stackalloc float[shCount * 3];
        int idx = shStartOffset;
        for (int i = 0; i < splatCount; ++i)
        {
            for (int j = 0; j < shCount; ++j)
            {
                tmp[j * 3 + 0] = data[idx + j];
                tmp[j * 3 + 1] = data[idx + j + shCount];
                tmp[j * 3 + 2] = data[idx + j + shCount * 2];
            }

            for (int j = 0; j < shCount * 3; ++j)
            {
                data[idx + j] = tmp[j];
            }

            idx += splatStride;
        }
    }

    [BurstCompile]
    struct CalcBoundsJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMin;
        [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMax;
        [ReadOnly] public NativeArray<InputSplatData> m_SplatData;

        public unsafe void Execute()
        {
            float3 boundsMin = float.PositiveInfinity;
            float3 boundsMax = float.NegativeInfinity;

            for (int i = 0; i < m_SplatData.Length; ++i)
            {
                float3 pos = m_SplatData[i].pos;
                boundsMin = math.min(boundsMin, pos);
                boundsMax = math.max(boundsMax, pos);
            }
            *m_BoundsMin = boundsMin;
            *m_BoundsMax = boundsMax;
        }
    }


    [BurstCompile]
    struct ReorderMortonJob : IJobParallelFor
    {
        const float kScaler = (float) ((1 << 21) - 1);
        public float3 m_BoundsMin;
        public float3 m_InvBoundsSize;
        [ReadOnly] public NativeArray<InputSplatData> m_SplatData;
        public NativeArray<(ulong,int)> m_Order;

        public void Execute(int index)
        {
            float3 pos = ((float3)m_SplatData[index].pos - m_BoundsMin) * m_InvBoundsSize * kScaler;
            uint3 ipos = (uint3) pos;
            ulong code = GaussianUtils.MortonEncode3(ipos);
            m_Order[index] = (code, index);
        }
    }

    struct OrderComparer : IComparer<(ulong, int)> {
        public int Compare((ulong, int) a, (ulong, int) b)
        {
            if (a.Item1 < b.Item1) return -1;
            if (a.Item1 > b.Item1) return +1;
            return a.Item2 - b.Item2;
        }
    }

    static void ReorderMorton(NativeArray<InputSplatData> splatData, float3 boundsMin, float3 boundsMax)
    {
        ReorderMortonJob order = new ReorderMortonJob
        {
            m_SplatData = splatData,
            m_BoundsMin = boundsMin,
            m_InvBoundsSize = 1.0f / (boundsMax - boundsMin),
            m_Order = new NativeArray<(ulong, int)>(splatData.Length, Allocator.TempJob)
        };
        order.Schedule(splatData.Length, 4096).Complete();
        order.m_Order.Sort(new OrderComparer());

        NativeArray<InputSplatData> copy = new(order.m_SplatData, Allocator.TempJob);
        for (int i = 0; i < copy.Length; ++i)
            order.m_SplatData[i] = copy[order.m_Order[i].Item2];
        copy.Dispose();

        order.m_Order.Dispose();
    }

    [BurstCompile]
    static unsafe void GatherSHs(int splatCount, InputSplatData* splatData, float* shData)
    {
        for (int i = 0; i < splatCount; ++i)
        {
            UnsafeUtility.MemCpy(shData, ((float*)splatData) + 9, 15 * 3 * sizeof(float));
            splatData++;
            shData += 15 * 3;
        }
    }

    [BurstCompile]
    struct ConvertSHClustersJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> m_Input;
        public NativeArray<GaussianSplatAsset.SHTableItemFloat16> m_Output;
        public void Execute(int index)
        {
            var addr = index * 15;
            GaussianSplatAsset.SHTableItemFloat16 res;
            res.sh1 = new half3(m_Input[addr+0]);
            res.sh2 = new half3(m_Input[addr+1]);
            res.sh3 = new half3(m_Input[addr+2]);
            res.sh4 = new half3(m_Input[addr+3]);
            res.sh5 = new half3(m_Input[addr+4]);
            res.sh6 = new half3(m_Input[addr+5]);
            res.sh7 = new half3(m_Input[addr+6]);
            res.sh8 = new half3(m_Input[addr+7]);
            res.sh9 = new half3(m_Input[addr+8]);
            res.shA = new half3(m_Input[addr+9]);
            res.shB = new half3(m_Input[addr+10]);
            res.shC = new half3(m_Input[addr+11]);
            res.shD = new half3(m_Input[addr+12]);
            res.shE = new half3(m_Input[addr+13]);
            res.shF = new half3(m_Input[addr+14]);
            res.shPadding = default;
            m_Output[index] = res;
        }
    }
    static bool ClusterSHProgress(float val)
    {
        EditorUtility.DisplayProgressBar(kProgressTitle, $"Cluster SHs ({val:P0})", 0.2f + val * 0.5f);
        return true;
    }

    static unsafe void ClusterSHs(NativeArray<InputSplatData> splatData, GaussianSplatAsset.SHFormat format, out NativeArray<GaussianSplatAsset.SHTableItemFloat16> shs, out NativeArray<int> shIndices)
    {
        shs = default;
        shIndices = default;

        int shCount = GaussianSplatAsset.GetSHCount(format, splatData.Length);
        if (shCount >= splatData.Length) // no need to cluster, just use raw data
            return;

        const int kShDim = 15 * 3;
        const int kBatchSize = 2048;
        float passesOverData = format switch
        {
            GaussianSplatAsset.SHFormat.Cluster64k => 0.3f,
            GaussianSplatAsset.SHFormat.Cluster32k => 0.4f,
            GaussianSplatAsset.SHFormat.Cluster16k => 0.5f,
            GaussianSplatAsset.SHFormat.Cluster8k => 0.8f,
            GaussianSplatAsset.SHFormat.Cluster4k => 1.2f,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };

        float t0 = Time.realtimeSinceStartup;
        NativeArray<float> shData = new(splatData.Length * kShDim, Allocator.Persistent);
        GatherSHs(splatData.Length, (InputSplatData*) splatData.GetUnsafeReadOnlyPtr(), (float*) shData.GetUnsafePtr());

        NativeArray<float> shMeans = new(shCount * kShDim, Allocator.Persistent);
        shIndices = new(splatData.Length, Allocator.Persistent);

        KMeansClustering.Calculate(kShDim, shData, kBatchSize, passesOverData, ClusterSHProgress, shMeans, shIndices);
        shData.Dispose();

        shs = new NativeArray<GaussianSplatAsset.SHTableItemFloat16>(shCount, Allocator.Persistent);

        ConvertSHClustersJob job = new ConvertSHClustersJob
        {
            m_Input = shMeans.Reinterpret<float3>(4),
            m_Output = shs
        };
        job.Schedule(shCount, 256).Complete();
        shMeans.Dispose();
        float t1 = Time.realtimeSinceStartup;
        //Debug.Log($"GS: clustered {splatData.Length/1000000.0:F2}M SHs into {shCount/1024}K ({passesOverData:F1}pass/{kBatchSize}batch) in {t1-t0:F0}s");
    }

    [BurstCompile]
    struct LinearizeDataJob : IJobParallelFor
    {
        public NativeArray<InputSplatData> splatData;
        public void Execute(int index)
        {
            var splat = splatData[index];

            // rot
            var q = splat.rot;
            var qq = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));
            qq = GaussianUtils.PackSmallest3Rotation(qq);
            splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

            // scale
            splat.scale = GaussianUtils.LinearScale(splat.scale);
            // transform scale to be more uniformly distributed
            splat.scale = math.pow(splat.scale, 1.0f / 8.0f);

            // color
            splat.dc0 = GaussianUtils.SH0ToColor(splat.dc0);
            splat.opacity = GaussianUtils.SquareCentered01(GaussianUtils.Sigmoid(splat.opacity));

            splatData[index] = splat;
        }
    }

    static void LinearizeData(NativeArray<InputSplatData> splatData)
    {
        LinearizeDataJob job = new LinearizeDataJob();
        job.splatData = splatData;
        job.Schedule(splatData.Length, 4096).Complete();
    }

    [BurstCompile]
    struct CalcChunkDataJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<InputSplatData> splatData;
        public NativeArray<GaussianSplatAsset.ChunkInfo> chunks;
        public bool keepRawSHs;

        public void Execute(int chunkIdx)
        {
            float3 chunkMinpos = float.PositiveInfinity;
            float3 chunkMinscl = float.PositiveInfinity;
            float4 chunkMincol = float.PositiveInfinity;
            float3 chunkMinshs = float.PositiveInfinity;
            float3 chunkMaxpos = float.NegativeInfinity;
            float3 chunkMaxscl = float.NegativeInfinity;
            float4 chunkMaxcol = float.NegativeInfinity;
            float3 chunkMaxshs = float.NegativeInfinity;

            int splatBegin = math.min(chunkIdx * GaussianSplatAsset.kChunkSize, splatData.Length);
            int splatEnd = math.min((chunkIdx + 1) * GaussianSplatAsset.kChunkSize, splatData.Length);

            // calculate data bounds inside the chunk
            for (int i = splatBegin; i < splatEnd; ++i)
            {
                InputSplatData s = splatData[i];
                chunkMinpos = math.min(chunkMinpos, s.pos);
                chunkMinscl = math.min(chunkMinscl, s.scale);
                chunkMincol = math.min(chunkMincol, new float4(s.dc0, s.opacity));
                chunkMinshs = math.min(chunkMinshs, s.sh1);
                chunkMinshs = math.min(chunkMinshs, s.sh2);
                chunkMinshs = math.min(chunkMinshs, s.sh3);
                chunkMinshs = math.min(chunkMinshs, s.sh4);
                chunkMinshs = math.min(chunkMinshs, s.sh5);
                chunkMinshs = math.min(chunkMinshs, s.sh6);
                chunkMinshs = math.min(chunkMinshs, s.sh7);
                chunkMinshs = math.min(chunkMinshs, s.sh8);
                chunkMinshs = math.min(chunkMinshs, s.sh9);
                chunkMinshs = math.min(chunkMinshs, s.shA);
                chunkMinshs = math.min(chunkMinshs, s.shB);
                chunkMinshs = math.min(chunkMinshs, s.shC);
                chunkMinshs = math.min(chunkMinshs, s.shD);
                chunkMinshs = math.min(chunkMinshs, s.shE);
                chunkMinshs = math.min(chunkMinshs, s.shF);

                chunkMaxpos = math.max(chunkMaxpos, s.pos);
                chunkMaxscl = math.max(chunkMaxscl, s.scale);
                chunkMaxcol = math.max(chunkMaxcol, new float4(s.dc0, s.opacity));
                chunkMaxshs = math.max(chunkMaxshs, s.sh1);
                chunkMaxshs = math.max(chunkMaxshs, s.sh2);
                chunkMaxshs = math.max(chunkMaxshs, s.sh3);
                chunkMaxshs = math.max(chunkMaxshs, s.sh4);
                chunkMaxshs = math.max(chunkMaxshs, s.sh5);
                chunkMaxshs = math.max(chunkMaxshs, s.sh6);
                chunkMaxshs = math.max(chunkMaxshs, s.sh7);
                chunkMaxshs = math.max(chunkMaxshs, s.sh8);
                chunkMaxshs = math.max(chunkMaxshs, s.sh9);
                chunkMaxshs = math.max(chunkMaxshs, s.shA);
                chunkMaxshs = math.max(chunkMaxshs, s.shB);
                chunkMaxshs = math.max(chunkMaxshs, s.shC);
                chunkMaxshs = math.max(chunkMaxshs, s.shD);
                chunkMaxshs = math.max(chunkMaxshs, s.shE);
                chunkMaxshs = math.max(chunkMaxshs, s.shF);
            }

            // store chunk info
            GaussianSplatAsset.ChunkInfo info = default;
            info.posX = new float2(chunkMinpos.x, chunkMaxpos.x);
            info.posY = new float2(chunkMinpos.y, chunkMaxpos.y);
            info.posZ = new float2(chunkMinpos.z, chunkMaxpos.z);
            info.sclX = math.f32tof16(chunkMinscl.x) | (math.f32tof16(chunkMaxscl.x) << 16);
            info.sclY = math.f32tof16(chunkMinscl.y) | (math.f32tof16(chunkMaxscl.y) << 16);
            info.sclZ = math.f32tof16(chunkMinscl.z) | (math.f32tof16(chunkMaxscl.z) << 16);
            info.colR = math.f32tof16(chunkMincol.x) | (math.f32tof16(chunkMaxcol.x) << 16);
            info.colG = math.f32tof16(chunkMincol.y) | (math.f32tof16(chunkMaxcol.y) << 16);
            info.colB = math.f32tof16(chunkMincol.z) | (math.f32tof16(chunkMaxcol.z) << 16);
            info.colA = math.f32tof16(chunkMincol.w) | (math.f32tof16(chunkMaxcol.w) << 16);
            info.shR = math.f32tof16(chunkMinshs.x) | (math.f32tof16(chunkMaxshs.x) << 16);
            info.shG = math.f32tof16(chunkMinshs.y) | (math.f32tof16(chunkMaxshs.y) << 16);
            info.shB = math.f32tof16(chunkMinshs.z) | (math.f32tof16(chunkMaxshs.z) << 16);
            chunks[chunkIdx] = info;

            // adjust data to be 0..1 within chunk bounds
            for (int i = splatBegin; i < splatEnd; ++i)
            {
                InputSplatData s = splatData[i];
                s.pos = ((float3)s.pos - chunkMinpos) / (chunkMaxpos - chunkMinpos);
                s.scale = ((float3)s.scale - chunkMinscl) / (chunkMaxscl - chunkMinscl);
                s.dc0 = ((float3)s.dc0 - chunkMincol.xyz) / (chunkMaxcol.xyz - chunkMincol.xyz);
                s.opacity = (s.opacity - chunkMincol.w) / (chunkMaxcol.w - chunkMincol.w);
                if (!keepRawSHs)
                {
                    s.sh1 = ((float3) s.sh1 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh2 = ((float3) s.sh2 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh3 = ((float3) s.sh3 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh4 = ((float3) s.sh4 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh5 = ((float3) s.sh5 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh6 = ((float3) s.sh6 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh7 = ((float3) s.sh7 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh8 = ((float3) s.sh8 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh9 = ((float3) s.sh9 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shA = ((float3) s.shA - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shB = ((float3) s.shB - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shC = ((float3) s.shC - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shD = ((float3) s.shD - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shE = ((float3) s.shE - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shF = ((float3) s.shF - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                }
                splatData[i] = s;
            }
        }
    }

    static GaussianSplatAssetByteBuffer CreateChunkData(NativeArray<InputSplatData> splatData, ref Hash128 dataHash, GaussianSplatAsset.SHFormat shFormat)
    {
        int chunkCount = (splatData.Length + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;
        CalcChunkDataJob job = new CalcChunkDataJob
        {
            splatData = splatData,
            chunks = new(chunkCount, Allocator.TempJob),
            keepRawSHs = shFormat == GaussianSplatAsset.SHFormat.Float32
        };
        job.Schedule(chunkCount, 8).Complete();

        dataHash.Append(ref job.chunks);

        byte[] res = job.chunks.Reinterpret<byte>(UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()).ToArray();
        job.chunks.Dispose();
        return GaussianSplatAssetByteBuffer.CreateContainer(res);
    }

    public static GraphicsFormat ColorFormatToGraphics(ColorFormat format)
    {
        return format switch
        {
            ColorFormat.Float32x4 => GraphicsFormat.R32G32B32A32_SFloat,
            ColorFormat.Float16x4 => GraphicsFormat.R16G16B16A16_SFloat,
            ColorFormat.Norm8x4 => GraphicsFormat.R8G8B8A8_UNorm,
            ColorFormat.BC7 => GraphicsFormat.RGBA_BC7_UNorm,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    [BurstCompile]
    struct ConvertColorJob : IJobParallelFor
    {
        public int width, height;
        [ReadOnly] public NativeArray<float4> inputData;
        [NativeDisableParallelForRestriction] public NativeArray<byte> outputData;
        public ColorFormat format;
        public int formatBytesPerPixel;

        public unsafe void Execute(int y)
        {
            int srcIdx = y * width;
            byte* dstPtr = (byte*) outputData.GetUnsafePtr() + y * width * formatBytesPerPixel;
            for (int x = 0; x < width; ++x)
            {
                float4 pix = inputData[srcIdx];

                switch (format)
                {
                    case ColorFormat.Float32x4:
                    {
                        *(float4*) dstPtr = pix;
                    }
                        break;
                    case ColorFormat.Float16x4:
                    {
                        half4 enc = new half4(pix);
                        *(half4*) dstPtr = enc;
                    }
                        break;
                    case ColorFormat.Norm8x4:
                    {
                        pix = math.saturate(pix);
                        uint enc = (uint)(pix.x * 255.5f) | ((uint)(pix.y * 255.5f) << 8) | ((uint)(pix.z * 255.5f) << 16) | ((uint)(pix.w * 255.5f) << 24);
                        *(uint*) dstPtr = enc;
                    }
                        break;
                }

                srcIdx++;
                dstPtr += formatBytesPerPixel;
            }
        }
    }

    static ulong EncodeFloat3ToNorm16(float3 v) // 48 bits: 16.16.16
    {
        return (ulong) (v.x * 65535.5f) | ((ulong) (v.y * 65535.5f) << 16) | ((ulong) (v.z * 65535.5f) << 32);
    }
    static uint EncodeFloat3ToNorm11(float3 v) // 32 bits: 11.10.11
    {
        return (uint) (v.x * 2047.5f) | ((uint) (v.y * 1023.5f) << 11) | ((uint) (v.z * 2047.5f) << 21);
    }
    static ushort EncodeFloat3ToNorm655(float3 v) // 16 bits: 6.5.5
    {
        return (ushort) ((uint) (v.x * 63.5f) | ((uint) (v.y * 31.5f) << 6) | ((uint) (v.z * 31.5f) << 11));
    }
    static ushort EncodeFloat3ToNorm565(float3 v) // 16 bits: 5.6.5
    {
        return (ushort) ((uint) (v.x * 31.5f) | ((uint) (v.y * 63.5f) << 5) | ((uint) (v.z * 31.5f) << 11));
    }

    static uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
    {
        return (uint) (v.x * 1023.5f) | ((uint) (v.y * 1023.5f) << 10) | ((uint) (v.z * 1023.5f) << 20) | ((uint) (v.w * 3.5f) << 30);
    }

    static unsafe void EmitEncodedVector(float3 v, byte* outputPtr, GaussianSplatAsset.VectorFormat format)
    {
        v = math.saturate(v);
        switch (format)
        {
            case GaussianSplatAsset.VectorFormat.Float32:
                {
                    *(float*) outputPtr = v.x;
                    *(float*) (outputPtr + 4) = v.y;
                    *(float*) (outputPtr + 8) = v.z;
                }
                break;
            case GaussianSplatAsset.VectorFormat.Norm16:
                {
                    ulong enc = EncodeFloat3ToNorm16(v);
                    *(uint*) outputPtr = (uint) enc;
                    *(ushort*) (outputPtr + 4) = (ushort) (enc >> 32);
                }
                break;
            case GaussianSplatAsset.VectorFormat.Norm11:
                {
                    uint enc = EncodeFloat3ToNorm11(v);
                    *(uint*) outputPtr = enc;
                }
                break;
            case GaussianSplatAsset.VectorFormat.Norm6:
                {
                    ushort enc = EncodeFloat3ToNorm655(v);
                    *(ushort*) outputPtr = enc;
                }
                break;
        }
    }

    [BurstCompile]
    struct CreatePositionsDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<InputSplatData> m_Input;
        public GaussianSplatAsset.VectorFormat m_Format;
        public int m_FormatSize;
        [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

        public unsafe void Execute(int index)
        {
            byte* outputPtr = (byte*) m_Output.GetUnsafePtr() + index * m_FormatSize;
            EmitEncodedVector(m_Input[index].pos, outputPtr, m_Format);
        }
    }

    [BurstCompile]
    struct CreateOtherDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<InputSplatData> m_Input;
        [NativeDisableContainerSafetyRestriction] [ReadOnly] public NativeArray<int> m_SplatSHIndices;
        public GaussianSplatAsset.VectorFormat m_ScaleFormat;
        public int m_FormatSize;
        [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

        public unsafe void Execute(int index)
        {
            byte* outputPtr = (byte*) m_Output.GetUnsafePtr() + index * m_FormatSize;

            // rotation: 4 bytes
            {
                Quaternion rotQ = m_Input[index].rot;
                float4 rot = new float4(rotQ.x, rotQ.y, rotQ.z, rotQ.w);
                uint enc = EncodeQuatToNorm10(rot);
                *(uint*) outputPtr = enc;
                outputPtr += 4;
            }

            // scale: 6, 4 or 2 bytes
            EmitEncodedVector(m_Input[index].scale, outputPtr, m_ScaleFormat);
            outputPtr += GaussianSplatAsset.GetVectorSize(m_ScaleFormat);

            // SH index
            if (m_SplatSHIndices.IsCreated)
                *(ushort*) outputPtr = (ushort)m_SplatSHIndices[index];
        }
    }

    GaussianSplatAssetByteBuffer CreatePositionsData(NativeArray<InputSplatData> inputSplats, ref Hash128 dataHash)
    {
        int dataLen = inputSplats.Length * GaussianSplatAsset.GetVectorSize(m_FormatPos);
        dataLen = (dataLen + 3) / 4 * 4; // multiple of 4
        NativeArray<byte> data = new(dataLen, Allocator.TempJob);

        CreatePositionsDataJob job = new CreatePositionsDataJob
        {
            m_Input = inputSplats,
            m_Format = m_FormatPos,
            m_FormatSize = GaussianSplatAsset.GetVectorSize(m_FormatPos),
            m_Output = data
        };
        job.Schedule(inputSplats.Length, 8192).Complete();

        dataHash.Append(data);
        byte[] res = data.ToArray();
        data.Dispose();
        return GaussianSplatAssetByteBuffer.CreateContainer(res);
    }

    GaussianSplatAssetByteBuffer CreateOtherData(NativeArray<InputSplatData> inputSplats, ref Hash128 dataHash, NativeArray<int> splatSHIndices)
    {
        int formatSize = GaussianSplatAsset.GetOtherSizeNoSHIndex(m_FormatScale);
        if (splatSHIndices.IsCreated)
            formatSize += 2;
        int dataLen = inputSplats.Length * formatSize;

        dataLen = (dataLen + 3) / 4 * 4; // multiple of 4
        NativeArray<byte> data = new(dataLen, Allocator.TempJob);

        CreateOtherDataJob job = new CreateOtherDataJob
        {
            m_Input = inputSplats,
            m_SplatSHIndices = splatSHIndices,
            m_ScaleFormat = m_FormatScale,
            m_FormatSize = formatSize,
            m_Output = data
        };
        job.Schedule(inputSplats.Length, 8192).Complete();

        dataHash.Append(data);
        byte[] res = data.ToArray();
        data.Dispose();
        return GaussianSplatAssetByteBuffer.CreateContainer(res);
    }

    static int SplatIndexToTextureIndex(uint idx)
    {
        uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
        uint width = GaussianSplatAsset.kTextureWidth / 16;
        idx >>= 8;
        uint x = (idx % width) * 16 + xy.x;
        uint y = (idx / width) * 16 + xy.y;
        return (int)(y * GaussianSplatAsset.kTextureWidth + x);
    }

    [BurstCompile]
    struct CreateColorDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<InputSplatData> m_Input;
        [NativeDisableParallelForRestriction] public NativeArray<float4> m_Output;

        public void Execute(int index)
        {
            var splat = m_Input[index];
            int i = SplatIndexToTextureIndex((uint)index);
            m_Output[i] = new float4(splat.dc0.x, splat.dc0.y, splat.dc0.z, splat.opacity);
        }
    }

    GaussianSplatAssetByteBuffer CreateColorData(NativeArray<InputSplatData> inputSplats, ref Hash128 dataHash, out int width, out int height, out GraphicsFormat format)
    {
        (width, height) = GaussianSplatAsset.CalcTextureSize(inputSplats.Length);
        NativeArray<float4> data = new(width * height, Allocator.TempJob);

        CreateColorDataJob jobCreate = new CreateColorDataJob
        {
            m_Input = inputSplats,
            m_Output = data
        };
        jobCreate.Schedule(inputSplats.Length, 8192).Complete();

        dataHash.Append(data);
        dataHash.Append((int)m_FormatColor);

        format = ColorFormatToGraphics(m_FormatColor);
        int dstSize = (int)GraphicsFormatUtility.ComputeMipmapSize(width, height, format);

        byte[] res;
        if (GraphicsFormatUtility.IsCompressedFormat(format))
        {
            Texture2D tex = new Texture2D(width, height, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);
            tex.SetPixelData(data, 0);
            EditorUtility.CompressTexture(tex, GraphicsFormatUtility.GetTextureFormat(format), 100);
            NativeArray<byte> cmpData = tex.GetPixelData<byte>(0);
            res = cmpData.ToArray();
            DestroyImmediate(tex);
        }
        else
        {
            ConvertColorJob jobConvert = new ConvertColorJob
            {
                width = width,
                height = height,
                inputData = data,
                format = m_FormatColor,
                outputData = new NativeArray<byte>(dstSize, Allocator.TempJob),
                formatBytesPerPixel = dstSize / width / height
            };
            jobConvert.Schedule(height, 1).Complete();
            res = jobConvert.outputData.ToArray();
            jobConvert.outputData.Dispose();
        }

        data.Dispose();

        return GaussianSplatAssetByteBuffer.CreateContainer(res);
    }

    [BurstCompile]
    struct CreateSHDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<InputSplatData> m_Input;
        public GaussianSplatAsset.SHFormat m_Format;
        public NativeArray<byte> m_Output;
        public void Execute(int index)
        {
            var splat = m_Input[index];
            switch (m_Format)
            {
                case GaussianSplatAsset.SHFormat.Float32:
                    {
                        GaussianSplatAsset.SHTableItemFloat32 res;
                        res.sh1 = splat.sh1;
                        res.sh2 = splat.sh2;
                        res.sh3 = splat.sh3;
                        res.sh4 = splat.sh4;
                        res.sh5 = splat.sh5;
                        res.sh6 = splat.sh6;
                        res.sh7 = splat.sh7;
                        res.sh8 = splat.sh8;
                        res.sh9 = splat.sh9;
                        res.shA = splat.shA;
                        res.shB = splat.shB;
                        res.shC = splat.shC;
                        res.shD = splat.shD;
                        res.shE = splat.shE;
                        res.shF = splat.shF;
                        res.shPadding = default;
                        var arr = m_Output.Reinterpret<GaussianSplatAsset.SHTableItemFloat32>(1);
                        arr[index] = res;
                    }
                    break;
                case GaussianSplatAsset.SHFormat.Float16:
                    {
                        GaussianSplatAsset.SHTableItemFloat16 res;
                        res.sh1 = new half3(splat.sh1);
                        res.sh2 = new half3(splat.sh2);
                        res.sh3 = new half3(splat.sh3);
                        res.sh4 = new half3(splat.sh4);
                        res.sh5 = new half3(splat.sh5);
                        res.sh6 = new half3(splat.sh6);
                        res.sh7 = new half3(splat.sh7);
                        res.sh8 = new half3(splat.sh8);
                        res.sh9 = new half3(splat.sh9);
                        res.shA = new half3(splat.shA);
                        res.shB = new half3(splat.shB);
                        res.shC = new half3(splat.shC);
                        res.shD = new half3(splat.shD);
                        res.shE = new half3(splat.shE);
                        res.shF = new half3(splat.shF);
                        res.shPadding = default;
                        var arr = m_Output.Reinterpret<GaussianSplatAsset.SHTableItemFloat16>(1);
                        arr[index] = res;
                    }
                    break;
                case GaussianSplatAsset.SHFormat.Norm11:
                    {
                        GaussianSplatAsset.SHTableItemNorm11 res;
                        res.sh1 = EncodeFloat3ToNorm11(splat.sh1);
                        res.sh2 = EncodeFloat3ToNorm11(splat.sh2);
                        res.sh3 = EncodeFloat3ToNorm11(splat.sh3);
                        res.sh4 = EncodeFloat3ToNorm11(splat.sh4);
                        res.sh5 = EncodeFloat3ToNorm11(splat.sh5);
                        res.sh6 = EncodeFloat3ToNorm11(splat.sh6);
                        res.sh7 = EncodeFloat3ToNorm11(splat.sh7);
                        res.sh8 = EncodeFloat3ToNorm11(splat.sh8);
                        res.sh9 = EncodeFloat3ToNorm11(splat.sh9);
                        res.shA = EncodeFloat3ToNorm11(splat.shA);
                        res.shB = EncodeFloat3ToNorm11(splat.shB);
                        res.shC = EncodeFloat3ToNorm11(splat.shC);
                        res.shD = EncodeFloat3ToNorm11(splat.shD);
                        res.shE = EncodeFloat3ToNorm11(splat.shE);
                        res.shF = EncodeFloat3ToNorm11(splat.shF);
                        var arr = m_Output.Reinterpret<GaussianSplatAsset.SHTableItemNorm11>(1);
                        arr[index] = res;
                    }
                    break;
                case GaussianSplatAsset.SHFormat.Norm6:
                    {
                        GaussianSplatAsset.SHTableItemNorm6 res;
                        res.sh1 = EncodeFloat3ToNorm565(splat.sh1);
                        res.sh2 = EncodeFloat3ToNorm565(splat.sh2);
                        res.sh3 = EncodeFloat3ToNorm565(splat.sh3);
                        res.sh4 = EncodeFloat3ToNorm565(splat.sh4);
                        res.sh5 = EncodeFloat3ToNorm565(splat.sh5);
                        res.sh6 = EncodeFloat3ToNorm565(splat.sh6);
                        res.sh7 = EncodeFloat3ToNorm565(splat.sh7);
                        res.sh8 = EncodeFloat3ToNorm565(splat.sh8);
                        res.sh9 = EncodeFloat3ToNorm565(splat.sh9);
                        res.shA = EncodeFloat3ToNorm565(splat.shA);
                        res.shB = EncodeFloat3ToNorm565(splat.shB);
                        res.shC = EncodeFloat3ToNorm565(splat.shC);
                        res.shD = EncodeFloat3ToNorm565(splat.shD);
                        res.shE = EncodeFloat3ToNorm565(splat.shE);
                        res.shF = EncodeFloat3ToNorm565(splat.shF);
                        res.shPadding = default;
                        var arr = m_Output.Reinterpret<GaussianSplatAsset.SHTableItemNorm6>(1);
                        arr[index] = res;
                    }
                    break;
                default:
                    break;
            }
        }
    }

    GaussianSplatAssetByteBuffer CreateSHData(NativeArray<InputSplatData> inputSplats, ref Hash128 dataHash, NativeArray<GaussianSplatAsset.SHTableItemFloat16> clusteredSHs)
    {
        if (clusteredSHs.IsCreated)
        {
            dataHash.Append(clusteredSHs);
            return GaussianSplatAssetByteBuffer.CreateContainer(clusteredSHs.Reinterpret<byte>(UnsafeUtility.SizeOf<GaussianSplatAsset.SHTableItemFloat16>()).ToArray());
        }

        int dataLen = (int)GaussianSplatAsset.CalcSHDataSize(inputSplats.Length, m_FormatSH);
        NativeArray<byte> data = new(dataLen, Allocator.TempJob);
        CreateSHDataJob job = new CreateSHDataJob
        {
            m_Input = inputSplats,
            m_Format = m_FormatSH,
            m_Output = data
        };
        job.Schedule(inputSplats.Length, 8192).Complete();
        dataHash.Append(data);
        byte[] res = data.ToArray();
        data.Dispose();
        return GaussianSplatAssetByteBuffer.CreateContainer(res);
    }

    static GaussianSplatAsset.CameraInfo[] LoadJsonCamerasFile(string curPath, bool doImport)
    {
        if (!doImport)
            return null;

        string camerasPath;
        while (true)
        {
            var dir = Path.GetDirectoryName(curPath);
            if (!Directory.Exists(dir))
                return null;
            camerasPath = $"{dir}/{kCamerasJson}";
            if (File.Exists(camerasPath))
                break;
            curPath = dir;
        }

        if (!File.Exists(camerasPath))
            return null;

        string json = File.ReadAllText(camerasPath);
        var jsonCameras = JSONParser.FromJson<List<JsonCamera>>(json);
        if (jsonCameras == null || jsonCameras.Count == 0)
            return null;

        var result = new GaussianSplatAsset.CameraInfo[jsonCameras.Count];
        for (var camIndex = 0; camIndex < jsonCameras.Count; camIndex++)
        {
            var jsonCam = jsonCameras[camIndex];
            var pos = new Vector3(jsonCam.position[0], jsonCam.position[1], jsonCam.position[2]);
            // the matrix is a "view matrix", not "camera matrix" lol
            var axisx = new Vector3(jsonCam.rotation[0][0], jsonCam.rotation[1][0], jsonCam.rotation[2][0]);
            var axisy = new Vector3(jsonCam.rotation[0][1], jsonCam.rotation[1][1], jsonCam.rotation[2][1]);
            var axisz = new Vector3(jsonCam.rotation[0][2], jsonCam.rotation[1][2], jsonCam.rotation[2][2]);

            axisy *= -1;
            axisz *= -1;

            var cam = new GaussianSplatAsset.CameraInfo
            {
                pos = pos,
                axisX = axisx,
                axisY = axisy,
                axisZ = axisz,
                fov = 25 //@TODO
            };
            result[camIndex] = cam;
        }

        return result;
    }

    [Serializable]
    public class JsonCamera
    {
        public int id;
        public string img_name;
        public int width;
        public int height;
        public float[] position;
        public float[][] rotation;
        public float fx;
        public float fy;
    }
}
