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
// - made buffer copy bit work with sizes larger than 4M.
// - fixed it to work on Metal (and possibly some Vulkan impls) at small item counts.

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
        /// <summary>Count, must be power of two.</summary>
        public uint             count;
        /// <summary>Defines the maximum height of the bitonic sort. Leave at zero for default full height.</summary>
        public uint             maxDepth;
        /// <summary>Keys</summary>
        public GraphicsBuffer   keys;
        /// <summary>Values</summary>
        public GraphicsBuffer   values;

        internal int workGroupCount;
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
            cmd.SetComputeIntParam(computeShader, "_Total", (int)args.count);
            cmd.SetComputeBufferParam(computeShader, 0, "_KeyBuffer", args.keys);
            cmd.SetComputeBufferParam(computeShader, 0, "_ValueBuffer", args.values);
            cmd.DispatchCompute(computeShader, 0, args.workGroupCount, 1, 1);
        }
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
        Assert.IsTrue(Mathf.IsPowerOfTwo((int)args.count));
        uint n = args.count;

        computeShader.GetKernelThreadGroupSizes(0, out var workGroupSizeX, out var workGroupSizeY, out var workGroupSizeZ);

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
