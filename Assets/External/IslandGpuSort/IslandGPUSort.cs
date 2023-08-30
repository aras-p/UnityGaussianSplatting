// This is adapted from Unity HDRP/SRP GPUSort code, which is over at:
//   https://github.com/Unity-Technologies/Graphics/tree/8bdd620b16/Packages/com.unity.render-pipelines.high-definition/Runtime/Utilities/GPUSort
//   https://github.com/Unity-Technologies/Graphics/tree/8bdd620b16/Packages/com.unity.render-pipelines.core/Runtime/Utilities/GPUSort
// however that code itself seems to be adapted from Tim Gfrerer "Project Island":
//   https://poniesandlight.co.uk/reflect/bitonic_merge_sort/
//   https://github.com/tgfrerer/island
// and that one is under MIT license, Copyright (c) 2020 Tim Gfrerer
//
// Adaptations done compared to HDRP code:
// - removed bits that were integrating into SRP RenderGraph thingy,
// - made it work with non-power of two data sizes (internally pads to next power of two with dummy values)
// - made buffer copy bit work with sizes larger than 4M.

using System;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

/// <summary>
/// Utility class for sorting (key, value) pairs on the GPU.
/// </summary>
public struct IslandGPUSort
{
    /// <summary>
    /// Data structure containing runtime dispatch parameters for the sort.
    /// </summary>
    public struct Args
    {
        /// <summary>Count</summary>
        public uint             count;
        /// <summary>Defines the maximum height of the bitonic sort. Leave at zero for default full height.</summary>
        public uint             maxDepth;
        /// <summary>Input Keys</summary>
        public GraphicsBuffer   inputKeys;
        /// <summary>Input Values</summary>
        public GraphicsBuffer   inputValues;
        /// <summary>Required runtime resources.</summary>
        public SupportResources resources;

        internal uint countPOT;
        internal int workGroupCount;
    }

    /// <summary>
    /// Data structure containing the runtime resources that are bound by the command buffer.
    /// </summary>
    public struct SupportResources
    {
        /// <summary>Sorted key buffer.</summary>
        public GraphicsBuffer sortBufferKeys;
        /// <summary>Sorted values buffer.</summary>
        public GraphicsBuffer sortBufferValues;

        public int origCount;

        /// <summary>
        /// Load supporting resources from Render Graph Resources.
        /// </summary>
        /// <param name="renderGraphResources">Render Graph Resources</param>
        /// <returns></returns>
        public static SupportResources Load(int count)
        {
            var targets = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopyDestination;

            var origCount = count;
            count = Mathf.NextPowerOfTwo(count);
            var resources = new SupportResources
            {
                sortBufferKeys   = new GraphicsBuffer(targets, count, 4) { name = "Keys" },
                sortBufferValues = new GraphicsBuffer(targets, count, 4) { name = "Values" },
                origCount = origCount
            };
            return resources;
        }

        /// <summary>
        /// Dispose the supporting resources.
        /// </summary>
        public void Dispose()
        {
            if (sortBufferKeys != null)
            {
                sortBufferKeys.Dispose();
                sortBufferKeys = null;
            }

            if (sortBufferValues != null)
            {
                sortBufferValues.Dispose();
                sortBufferValues = null;
            }
        }
    }

    private LocalKeyword[] m_Keywords;

    enum Stage
    {
        LocalBMS,
        LocalDisperse,
        BigFlip,
        BigDisperse
    }

    ComputeShader computeShader;

    /// Initializes a re-usable GPU sorting instance.
    public IslandGPUSort(ComputeShader cs)
    {
        computeShader = cs;
        m_Keywords = new LocalKeyword[4]
        {
            new(cs, "STAGE_BMS"),
            new(cs, "STAGE_LOCAL_DISPERSE"),
            new(cs, "STAGE_BIG_FLIP"),
            new(cs, "STAGE_BIG_DISPERSE")
        };
    }

    void DispatchStage(CommandBuffer cmd, Args args, uint h, Stage stage)
    {
        Assert.IsTrue(args.workGroupCount != -1);
        Assert.IsNotNull(computeShader);
        {
#if false
            m_SortCS.enabledKeywords = new[]  { keywords[(int)stage] };
#else
            // Unfortunately need to configure the keywords like this. Might be worth just having a kernel per stage.
            foreach (var k in m_Keywords)
                cmd.SetKeyword(computeShader, k, false);
            cmd.SetKeyword(computeShader, m_Keywords[(int) stage], true);
#endif

            cmd.SetComputeIntParam(computeShader, "_H", (int) h);
            cmd.SetComputeIntParam(computeShader, "_Total", (int)args.countPOT);
            cmd.SetComputeBufferParam(computeShader, 0, "_KeyBuffer", args.resources.sortBufferKeys);
            cmd.SetComputeBufferParam(computeShader, 0, "_ValueBuffer", args.resources.sortBufferValues);
            cmd.DispatchCompute(computeShader, 0, args.workGroupCount, 1, 1);
        }
    }

    void CopyBuffer(CommandBuffer cmd, GraphicsBuffer src, GraphicsBuffer dst, int origCount)
    {
        //disable all keywords for copy
        foreach (var k in m_Keywords)
            cmd.SetKeyword(computeShader, k, false);

        int entriesToCopy = src.count * src.stride / 4;
        cmd.SetComputeBufferParam(computeShader, 1, "_CopySrcBuffer", src);
        cmd.SetComputeBufferParam(computeShader, 1, "_CopyDstBuffer", dst);
        cmd.SetComputeIntParam(computeShader, "_OrigEntriesCount", origCount);
        cmd.SetComputeIntParam(computeShader, "_CopyEntriesCount", entriesToCopy);
        cmd.DispatchCompute(computeShader, 1, (entriesToCopy + 1023) / 1024, 1, 1);
    }

    static int DivRoundUp(int x, int y) => (x + y - 1) / y;

    /// <summary>
    /// Sorts a list of (key, value) pairs.
    /// </summary>
    /// <param name="cmd">Command buffer for recording the sorting commands.</param>
    /// <param name="args">Runtime arguments for the sorting.</param>
    public void Dispatch(CommandBuffer cmd, Args args)
    {
        Assert.IsNotNull(computeShader);
        uint n = (uint)Mathf.NextPowerOfTwo((int)args.count);

        CopyBuffer(cmd, args.inputKeys, args.resources.sortBufferKeys, args.resources.origCount);
        CopyBuffer(cmd, args.inputValues, args.resources.sortBufferValues, args.resources.origCount);
        
        computeShader.GetKernelThreadGroupSizes(0, out var workGroupSizeX, out var workGroupSizeY, out var workGroupSizeZ);

        args.countPOT = n;
        args.workGroupCount = Math.Max(1, DivRoundUp((int) n, (int) workGroupSizeX * 2));

        if (args.maxDepth == 0 || args.maxDepth > n)
            args.maxDepth = n;
        uint h = Math.Min(workGroupSizeX * 2, args.maxDepth);

        DispatchStage(cmd, args, h, Stage.LocalBMS);

        h *= 2;

        for (; h <= Math.Min(n, args.maxDepth); h *= 2)
        {
            DispatchStage(cmd, args, h, Stage.BigFlip);

            for (uint hh = h / 2; hh > 1; hh /= 2)
            {
                if (hh <= workGroupSizeX * 2)
                {
                    DispatchStage(cmd, args, hh, Stage.LocalDisperse);
                    break;
                }

                DispatchStage(cmd, args, hh, Stage.BigDisperse);
            }
        }
    }
}
