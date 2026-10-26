using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // GPU (uint key, uint payload) 8 bit-LSD radix sort, using reduce-then-scan
    // Copyright Thomas Smith 2024, MIT license
    // https://github.com/b0nes164/GPUSorting

    public class GpuSorting
    {
        //The size of a threadblock partition in the sort
        const uint DEVICE_RADIX_SORT_PARTITION_SIZE = 3840;

        //The size of our radix in bits
        const uint DEVICE_RADIX_SORT_BITS = 8;

        //Number of digits in our radix, 1 << DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_RADIX = 256;

        //Number of sorting passes required to sort a 32bit key, KEY_BITS / DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_PASSES = 4;

        //Keywords to enable for the shader
        private LocalKeyword m_keyUintKeyword;
        private LocalKeyword m_payloadUintKeyword;
        private LocalKeyword m_ascendKeyword;
        private LocalKeyword m_sortPairKeyword;
        private LocalKeyword m_vulkanKeyword;

        public struct Args
        {
            public uint             count;
            public GraphicsBuffer   inputKeys;
            public GraphicsBuffer   inputValues;
            public SupportResources resources;
            internal int workGroupCount;
        }

        public struct SupportResources
        {
            public GraphicsBuffer altBuffer;
            public GraphicsBuffer altPayloadBuffer;
            public GraphicsBuffer passHistBuffer;
            public GraphicsBuffer globalHistBuffer;
            public GraphicsBuffer dispatchArgs;

            public static SupportResources Load(uint count)
            {
                //This is threadBlocks * DEVICE_RADIX_SORT_RADIX
                uint scratchBufferSize = DivRoundUp(count, DEVICE_RADIX_SORT_PARTITION_SIZE) * DEVICE_RADIX_SORT_RADIX; 
                uint reducedScratchBufferSize = DEVICE_RADIX_SORT_RADIX * DEVICE_RADIX_SORT_PASSES;

                var target = GraphicsBuffer.Target.Structured;
                var resources = new SupportResources
                {
                    altBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "DeviceRadixAlt" },
                    altPayloadBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "DeviceRadixAltPayload" },
                    passHistBuffer = new GraphicsBuffer(target, (int)scratchBufferSize, 4) { name = "DeviceRadixPassHistogram" },
                    globalHistBuffer = new GraphicsBuffer(target, (int)reducedScratchBufferSize, 4) { name = "DeviceRadixGlobalHistogram" },
                    dispatchArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 3, 4) { name = "DeviceRadixDispatchArgs" }
                };
                return resources;
            }

            public void Dispose()
            {
                altBuffer?.Dispose();
                altPayloadBuffer?.Dispose();
                passHistBuffer?.Dispose();
                globalHistBuffer?.Dispose();
                dispatchArgs?.Dispose();

                altBuffer = null;
                altPayloadBuffer = null;
                passHistBuffer = null;
                globalHistBuffer = null;
                dispatchArgs = null;
            }
        }


        readonly ComputeShader m_CS;
        readonly int m_kernelInitDeviceRadixSort = -1;
        readonly int m_kernelUpsweep = -1;
        readonly int m_kernelScan = -1;
        readonly int m_kernelDownsweep = -1;
        readonly int m_kernelBuildIndirectArgs = -1;

        readonly bool m_Valid;

        public bool Valid => m_Valid;

        public GpuSorting(ComputeShader cs)
        {
            m_CS = cs;
            if (cs)
            {
                m_kernelInitDeviceRadixSort = cs.FindKernel("InitDeviceRadixSort");
                m_kernelUpsweep = cs.FindKernel("Upsweep");
                m_kernelScan = cs.FindKernel("Scan");
                m_kernelDownsweep = cs.FindKernel("Downsweep");
                m_kernelBuildIndirectArgs = cs.FindKernel("BuildIndirectArgs");
            }

            m_Valid = m_kernelInitDeviceRadixSort >= 0 &&
                      m_kernelUpsweep >= 0 &&
                      m_kernelScan >= 0 &&
                      m_kernelDownsweep >= 0 &&
                      m_kernelBuildIndirectArgs >= 0;
            if (m_Valid)
            {
                if (!cs.IsSupported(m_kernelInitDeviceRadixSort) ||
                    !cs.IsSupported(m_kernelUpsweep) ||
                    !cs.IsSupported(m_kernelScan) ||
                    !cs.IsSupported(m_kernelDownsweep) || 
                    !cs.IsSupported(m_kernelBuildIndirectArgs))
                {
                    m_Valid = false;
                }
            }

            m_keyUintKeyword = new LocalKeyword(cs, "KEY_UINT");
            m_payloadUintKeyword = new LocalKeyword(cs, "PAYLOAD_UINT");
            m_ascendKeyword = new LocalKeyword(cs, "SHOULD_ASCEND");
            m_sortPairKeyword = new LocalKeyword(cs, "SORT_PAIRS");
            m_vulkanKeyword = new LocalKeyword(cs, "VULKAN");

            

            cs.EnableKeyword(m_keyUintKeyword);
            cs.EnableKeyword(m_payloadUintKeyword);
            cs.EnableKeyword(m_ascendKeyword);
            cs.EnableKeyword(m_sortPairKeyword);
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
                cs.EnableKeyword(m_vulkanKeyword);
            else
                cs.DisableKeyword(m_vulkanKeyword);
        }

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        public void DispatchIndirect(CommandBuffer cmd, Args args, GraphicsBuffer sortCountBuffer)
        {
            Assert.IsTrue(Valid);

            GraphicsBuffer srcKeyBuffer = args.inputKeys;
            GraphicsBuffer srcPayloadBuffer = args.inputValues;
            GraphicsBuffer dstKeyBuffer = args.resources.altBuffer;
            GraphicsBuffer dstPayloadBuffer = args.resources.altPayloadBuffer;

            uint maxCount = args.count;
            uint maxThreadBlocks = DivRoundUp(maxCount, DEVICE_RADIX_SORT_PARTITION_SIZE);

            // Keep max width for histogram layout.
            cmd.SetComputeIntParam(m_CS, "e_numKeys", (int)maxCount);
            cmd.SetComputeIntParam(m_CS, "e_threadBlocks", (int)maxThreadBlocks);

            // --- Build indirect args on GPU ---
            cmd.SetComputeBufferParam(m_CS, m_kernelBuildIndirectArgs, "b_sortCount", sortCountBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelBuildIndirectArgs, "b_dispatchArgs", args.resources.dispatchArgs);
            cmd.DispatchCompute(m_CS, m_kernelBuildIndirectArgs, 1, 1, 1);

            // Bind size buffers to the kernels
            cmd.SetComputeBufferParam(m_CS, m_kernelScan, "b_dispatchArgs", args.resources.dispatchArgs);
            cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep,   "b_sortCount", sortCountBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sortCount", sortCountBuffer);

            // Static buffers for sort kernels
            cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep,   "b_passHist", args.resources.passHistBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep,   "b_globalHist", args.resources.globalHistBuffer);

            cmd.SetComputeBufferParam(m_CS, m_kernelScan,      "b_passHist", args.resources.passHistBuffer);

            cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_passHist", args.resources.passHistBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_globalHist", args.resources.globalHistBuffer);

            // Clear global histogram
            cmd.SetComputeBufferParam(m_CS, m_kernelInitDeviceRadixSort, "b_globalHist", args.resources.globalHistBuffer);
            cmd.DispatchCompute(m_CS, m_kernelInitDeviceRadixSort, 1, 1, 1);

            // Execute the sort algorithm in 8-bit increments
            for (uint radixShift = 0; radixShift < 32; radixShift += DEVICE_RADIX_SORT_BITS)
            {
                cmd.SetComputeIntParam(m_CS, "e_radixShift", (int)radixShift);

                // Upsweep (INDIRECT)
                cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_sort", srcKeyBuffer);
                cmd.DispatchCompute(m_CS, m_kernelUpsweep, args.resources.dispatchArgs, 0);

                // Scan (fixed 256 groups)
                cmd.DispatchCompute(m_CS, m_kernelScan, (int)DEVICE_RADIX_SORT_RADIX, 1, 1);

                // Downsweep (INDIRECT)
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sort", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sortPayload", srcPayloadBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_alt", dstKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_altPayload", dstPayloadBuffer);
                cmd.DispatchCompute(m_CS, m_kernelDownsweep, args.resources.dispatchArgs, 0);

                // Swap
                (srcKeyBuffer, dstKeyBuffer) = (dstKeyBuffer, srcKeyBuffer);
                (srcPayloadBuffer, dstPayloadBuffer) = (dstPayloadBuffer, srcPayloadBuffer);
            }
        }


    }
}
