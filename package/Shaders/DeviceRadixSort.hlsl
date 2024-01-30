/******************************************************************************
 * Device Level 8-bit LSD Radix Sort using reduce then scan
 *
 * SPDX-License-Identifier: MIT
 * Copyright Thomas Smith 12/6/2023
 *  
 *  Permission is hereby granted, free of charge, to any person obtaining a copy
 *  of this software and associated documentation files (the "Software"), to deal
 *  in the Software without restriction, including without limitation the rights
 *  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 *  copies of the Software, and to permit persons to whom the Software is
 *  furnished to do so, subject to the following conditions:
 *
 *  The above copyright notice and this permission notice shall be included in all
 *  copies or substantial portions of the Software.
 *
 *  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 *  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 *  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 *  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 *  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 *  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 *  SOFTWARE.
 ******************************************************************************/

//General macros 
#define PARTITION_SIZE      3840U   //size of a partition tile

#define UPSWEEP_THREADS     128U    //The number of threads in a Upsweep threadblock
#define SCAN_THREADS        128U    //The number of threads in a Scan threadblock
#define DS_THREADS          256U    //The number of threads in a Downsweep threadblock

#define RADIX               256U    //Number of digit bins
#define RADIX_MASK          255U    //Mask of digit bins
#define RADIX_LOG           8U      //log2(RADIX)

//For smaller waves where bit packing is necessary
#define HALF_RADIX          128U    
#define HALF_MASK           127U

#define WAVE_INDEX          (gtid.x / WaveGetLaneCount())                   //The wave that a thread belongs to
#define PARTITION_START     (gid.x * PARTITION_SIZE)                        //The start of threadblock partition tile

//For the downsweep kernels
#define DS_KEYS_PER_THREAD  15U                                             //The number of keys per thread in a Downsweep Threadblock
#define MAX_DS_SMEM         4096U                                           //shared memory for downsweep kernel
#define DS_WGE16_PART_SIZE  (DS_KEYS_PER_THREAD * WaveGetLaneCount())       //The size of a wave partition for wave size 16 or greater
#define DS_WGE16_PART_START (WAVE_INDEX * DS_WGE16_PART_SIZE)               //The starting offset of a wave partition for wave size 16 or greater
#define DS_WGE16_HISTS_SIZE (DS_THREADS / WaveGetLaneCount() * RADIX)       //The total size of all wave histograms in shared memory for wave size 16 or greater

#define DS_WLT16_PART_SIZE  (DS_KEYS_PER_THREAD * WaveGetLaneCount() * serialIterations)    //The size of a "combined" wave partition for wave size less than 16
#define DS_WLT16_PART_START ((WAVE_INDEX / serialIterations * DS_WLT16_PART_SIZE) + \
                            ((WAVE_INDEX % serialIterations) * WaveGetLaneCount()))         //The starting offset of a "combined" wave partition for wave size less than 16
#define DS_WLT16_HISTS_SIZE MAX_DS_SMEM                                                     //The total size of all wave histograms in shared memory for wave size less than 16

cbuffer cbParallelSort : register(b0)
{
    uint e_numKeys;
    uint e_radixShift;
    uint e_threadBlocks;
    uint padding0;
    uint padding1;
    uint padding2;
    uint padding3;
    uint padding4;
};

RWStructuredBuffer<uint> b_sort;            //buffer to be sorted
RWStructuredBuffer<uint> b_sortPayload;     //payload buffer
RWStructuredBuffer<uint> b_alt;             //double buffer
RWStructuredBuffer<uint> b_altPayload;      //double buffer payload
RWStructuredBuffer<uint> b_globalHist;      //buffer holding device level offsets for each binning pass
RWStructuredBuffer<uint> b_passHist;        //buffer used to store reduced sums of partition tiles

groupshared uint g_upsweepHist[RADIX * 2];  //Shared memory for upsweep
groupshared uint g_scanMem[SCAN_THREADS];   //Shared memory for the scan
groupshared uint g_dsMem[MAX_DS_SMEM];      //Shared memory for the downsweep

//Clear the global histogram, as we will be adding to it atomically
[numthreads(1024, 1, 1)]
void InitDeviceRadixSort(int3 id : SV_DispatchThreadID)
{
    b_globalHist[id.x] = 0;
}

[numthreads(UPSWEEP_THREADS, 1, 1)]
void Upsweep(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    //clear shared memory
    {
        const uint histsEnd = RADIX * 2;
        for (uint i = gtid.x; i < histsEnd; i += UPSWEEP_THREADS)
            g_upsweepHist[i] = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    //histogram, 64 threads to a histogram
    {
        const uint radixShift = e_radixShift;
        const uint offset = gtid.x / 64 * RADIX;
        const uint partitionEnd = gid.x == e_threadBlocks - 1 ? e_numKeys : (gid.x + 1) * PARTITION_SIZE;
        for (uint i = gtid.x + PARTITION_START; i < partitionEnd; i += UPSWEEP_THREADS)
            InterlockedAdd(g_upsweepHist[(b_sort[i] >> radixShift & RADIX_MASK) + offset], 1);
    }
    GroupMemoryBarrierWithGroupSync();
    
    //reduce and pass to tile histogram
    {
        const uint threadBlocks = e_threadBlocks;
        for (uint i = gtid.x; i < RADIX; i += UPSWEEP_THREADS)
        {
            g_upsweepHist[i] += g_upsweepHist[i + RADIX];
            b_passHist[i * threadBlocks + gid.x] = g_upsweepHist[i];
        }
    }
    
    //Larger 16 or greater can perform a more elegant scan because 16 * 16 = 256
    if (WaveGetLaneCount() >= 16)
    {
        for (uint i = gtid.x; i < RADIX; i += UPSWEEP_THREADS)
            g_upsweepHist[i] += WavePrefixSum(g_upsweepHist[i]);
        GroupMemoryBarrierWithGroupSync();
        
        if (gtid.x < (RADIX / WaveGetLaneCount()))
            g_upsweepHist[(gtid.x + 1) * WaveGetLaneCount() - 1] += WavePrefixSum(g_upsweepHist[(gtid.x + 1) * WaveGetLaneCount() - 1]);
        GroupMemoryBarrierWithGroupSync();
        
        //atomically add to global histogram
        const uint offset = e_radixShift << 5;
        const uint laneMask = WaveGetLaneCount() - 1;
        const uint circularLaneShift = WaveGetLaneIndex() + 1 & laneMask;
        for (uint i = gtid.x; i < RADIX; i += UPSWEEP_THREADS)
        {
            const uint index = circularLaneShift + (i & ~laneMask);
            InterlockedAdd(b_globalHist[index + offset], (WaveGetLaneIndex() != laneMask ? g_upsweepHist[i] : 0) +
                (i >= WaveGetLaneCount() ? WaveReadLaneAt(g_upsweepHist[i - 1], 0) : 0));
        }
    }
    
    //Exclusive Brent-Kung with fused upsweep downsweep
    if (WaveGetLaneCount() < 16)
    {
        const uint deviceOffset = e_radixShift << 5;
        for (uint i = gtid.x; i < RADIX; i += UPSWEEP_THREADS)
            g_upsweepHist[i] += WavePrefixSum(g_upsweepHist[i]);
        
        if (gtid.x < WaveGetLaneCount())
            InterlockedAdd(b_globalHist[gtid.x + deviceOffset], gtid.x ? g_upsweepHist[gtid.x - 1] : 0);
        GroupMemoryBarrierWithGroupSync();
        
        const uint laneLog = countbits(WaveGetLaneCount() - 1);
        uint offset = laneLog;
        uint j = WaveGetLaneCount();
        for (; j < (RADIX >> 1); j <<= laneLog)
        {
            for (uint i = gtid.x; i < (RADIX >> offset); i += UPSWEEP_THREADS)
                g_upsweepHist[((i + 1) << offset) - 1] += WavePrefixSum(g_upsweepHist[((i + 1) << offset) - 1]);
            GroupMemoryBarrierWithGroupSync();
            
            for (uint i = gtid.x + j; i < RADIX; i += UPSWEEP_THREADS)
            {
                if ((i & ((j << laneLog) - 1)) >= j)
                {
                    if (i < (j << laneLog))
                    {
                        InterlockedAdd(b_globalHist[i + deviceOffset],
                            WaveReadLaneAt(g_upsweepHist[((i >> offset) << offset) - 1], 0) +
                            ((i & (j - 1)) ? g_upsweepHist[i - 1] : 0));
                    }
                    else
                    {
                        if ((i + 1) & (j - 1))
                            g_upsweepHist[i] += WaveReadLaneAt(g_upsweepHist[((i >> offset) << offset) - 1], 0);
                    }
                }
            }
            offset += laneLog;
        }
        GroupMemoryBarrierWithGroupSync();
        
        for (uint i = gtid.x + j; i < RADIX; i += UPSWEEP_THREADS)
        {
            InterlockedAdd(b_globalHist[i + deviceOffset],
                WaveReadLaneAt(g_upsweepHist[((i >> offset) << offset) - 1], 0) +
                ((i & (j - 1)) ? g_upsweepHist[i - 1] : 0));
        }
    }
}

//Scan along the spine of the upsweep
[numthreads(SCAN_THREADS, 1, 1)]
void Scan(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    if (WaveGetLaneCount() >= 16)
    {
        uint aggregate = 0;
        const uint laneMask = WaveGetLaneCount() - 1;
        const uint circularLaneShift = WaveGetLaneIndex() + 1 & laneMask;
        const uint partionsEnd = e_threadBlocks / SCAN_THREADS * SCAN_THREADS;
        const uint offset = gid.x * e_threadBlocks;
        uint i = gtid.x;
        for (; i < partionsEnd; i += SCAN_THREADS)
        {
            g_scanMem[gtid.x] = b_passHist[i + offset];
            g_scanMem[gtid.x] += WavePrefixSum(g_scanMem[gtid.x]);
            GroupMemoryBarrierWithGroupSync();
            
            if (gtid.x < SCAN_THREADS / WaveGetLaneCount())
                g_scanMem[(gtid.x + 1) * WaveGetLaneCount() - 1] += WavePrefixSum(g_scanMem[(gtid.x + 1) * WaveGetLaneCount() - 1]);
            GroupMemoryBarrierWithGroupSync();
            
            b_passHist[circularLaneShift + (i & ~laneMask) + offset] = (WaveGetLaneIndex() != laneMask ? g_scanMem[gtid.x] : 0) +
                (gtid.x >= WaveGetLaneCount() ? WaveReadLaneAt(g_scanMem[gtid.x - 1], 0) : 0) + aggregate;

            aggregate += g_scanMem[SCAN_THREADS - 1];
            GroupMemoryBarrierWithGroupSync();
        }
        
        //partial
        if (i < e_threadBlocks)
            g_scanMem[gtid.x] = b_passHist[offset + i];
        g_scanMem[gtid.x] += WavePrefixSum(g_scanMem[gtid.x]);
        GroupMemoryBarrierWithGroupSync();
            
        if (gtid.x < SCAN_THREADS / WaveGetLaneCount())
            g_scanMem[(gtid.x + 1) * WaveGetLaneCount() - 1] += WavePrefixSum(g_scanMem[(gtid.x + 1) * WaveGetLaneCount() - 1]);
        GroupMemoryBarrierWithGroupSync();
        
        const uint index = circularLaneShift + (i & ~laneMask);
        if (index < e_threadBlocks)
        {
            b_passHist[index + offset] = (WaveGetLaneIndex() != laneMask ? g_scanMem[gtid.x] : 0) +
                (gtid.x >= WaveGetLaneCount() ? g_scanMem[(gtid.x & ~laneMask) - 1] : 0) + aggregate;
        }
    }

    if (WaveGetLaneCount() < 16)
    {
        uint aggregate = 0;
        const uint partitions = e_threadBlocks / SCAN_THREADS;
        const uint deviceOffset = gid.x * e_threadBlocks;
        const uint laneLog = countbits(WaveGetLaneCount() - 1);
        
        uint k = 0;
        for (; k < partitions; ++k)
        {
            g_scanMem[gtid.x] = b_passHist[gtid.x + k * SCAN_THREADS + deviceOffset];
            g_scanMem[gtid.x] += WavePrefixSum(g_scanMem[gtid.x]);
            
            if (gtid.x < WaveGetLaneCount())
                b_passHist[gtid.x + k * SCAN_THREADS + deviceOffset] = (gtid.x ? g_scanMem[gtid.x - 1] : 0) + aggregate;
            GroupMemoryBarrierWithGroupSync();
            
            uint offset = laneLog;
            uint j = WaveGetLaneCount();
            for (; j < (SCAN_THREADS >> 1); j <<= laneLog)
            {
                for (uint i = gtid.x; i < (SCAN_THREADS >> offset); i += SCAN_THREADS)
                    g_scanMem[((i + 1) << offset) - 1] += WavePrefixSum(g_scanMem[((i + 1) << offset) - 1]);
                GroupMemoryBarrierWithGroupSync();
            
                for (uint i = gtid.x + j; i < SCAN_THREADS; i += SCAN_THREADS)
                {
                    if ((i & ((j << laneLog) - 1)) >= j)
                    {
                        if (i < (j << laneLog))
                        {
                            b_passHist[i + k * SCAN_THREADS + deviceOffset] = WaveReadLaneAt(g_scanMem[((i >> offset) << offset) - 1], 0) +
                                ((i & (j - 1)) ? g_scanMem[i - 1] : 0) + aggregate;
                        }
                        else
                        {
                            if ((i + 1) & (j - 1))
                                g_scanMem[i] += WaveReadLaneAt(g_scanMem[((i >> offset) << offset) - 1], 0);
                        }
                    }
                }
                offset += laneLog;
            }
            GroupMemoryBarrierWithGroupSync();
        
            for (uint i = gtid.x + j; i < SCAN_THREADS; i += SCAN_THREADS)
            {
                b_passHist[i + k * SCAN_THREADS + deviceOffset] = WaveReadLaneAt(g_scanMem[((i >> offset) << offset) - 1], 0) +
                            ((i & (j - 1)) ? g_scanMem[i - 1] : 0) + aggregate;
            }
            
            aggregate += WaveReadLaneAt(g_scanMem[SCAN_THREADS - 1], 0) + WaveReadLaneAt(g_scanMem[(((SCAN_THREADS - 1) >> offset) << offset) - 1], 0);
            GroupMemoryBarrierWithGroupSync();
        }
        
        //partial
        const uint finalPartSize = e_threadBlocks - k * SCAN_THREADS;
        if (gtid.x < finalPartSize)
        {
            g_scanMem[gtid.x] = b_passHist[gtid.x + k * SCAN_THREADS + deviceOffset];
            g_scanMem[gtid.x] += WavePrefixSum(g_scanMem[gtid.x]);
            
            if (gtid.x < WaveGetLaneCount())
                b_passHist[gtid.x + k * SCAN_THREADS + deviceOffset] = (gtid.x ? g_scanMem[gtid.x - 1] : 0) + aggregate;
        }
        GroupMemoryBarrierWithGroupSync();
        
        uint offset = laneLog;
        for (uint j = WaveGetLaneCount(); j < finalPartSize; j <<= laneLog)
        {
            for (uint i = gtid.x; i < (finalPartSize >> offset); i += SCAN_THREADS)
                g_scanMem[((i + 1) << offset) - 1] += WavePrefixSum(g_scanMem[((i + 1) << offset) - 1]);
            GroupMemoryBarrierWithGroupSync();
            
            for (uint i = gtid.x + j; i < finalPartSize; i += SCAN_THREADS)
            {
                if ((i & ((j << laneLog) - 1)) >= j)
                {
                    if (i < (j << laneLog))
                    {
                        b_passHist[i + k * SCAN_THREADS + deviceOffset] = WaveReadLaneAt(g_scanMem[((i >> offset) << offset) - 1], 0) +
                            ((i & (j - 1)) ? g_scanMem[i - 1] : 0) + aggregate;
                    }
                    else
                    {
                        if ((i + 1) & (j - 1))
                            g_scanMem[i] += WaveReadLaneAt(g_scanMem[((i >> offset) << offset) - 1], 0);
                    }
                }
            }
            offset += laneLog;
        }
    }
}

[numthreads(DS_THREADS, 1, 1)]
void Downsweep(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    if (gid.x < e_threadBlocks - 1)
    {
        uint keys[DS_KEYS_PER_THREAD];
        uint offsets[DS_KEYS_PER_THREAD];
        uint exclusiveWaveReduction;
        
        if (WaveGetLaneCount() >= 16)
        {
            //Load keys into registers
            [unroll]
            for (uint i = 0, t = WaveGetLaneIndex() + DS_WGE16_PART_START + PARTITION_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount())
                keys[i] = b_sort[t];
            
            //Clear histogram memory
            {
                const uint downsweepHists = DS_WGE16_HISTS_SIZE;
                for (uint i = gtid.x; i < downsweepHists; i += DS_THREADS)
                    g_dsMem[i] = 0;
            }
            GroupMemoryBarrierWithGroupSync();

            //Warp Level Multisplit
            {
                const uint waveParts = (WaveGetLaneCount() + 31) / 32;
                uint4 waveFlags;
            
                [unroll]
                for (uint i = 0; i < DS_KEYS_PER_THREAD; ++i)
                {
                    waveFlags = (WaveGetLaneCount() & 31) ? (1U << WaveGetLaneCount()) - 1 : 0xffffffff;

                    for (uint k = e_radixShift; k < e_radixShift + RADIX_LOG; ++k)
                    {
                        const bool t = keys[i] >> k & 1;
                        const uint4 ballot = WaveActiveBallot(t);
                        for (int wavePart = 0; wavePart < waveParts; ++wavePart)
                            waveFlags[wavePart] &= (t ? 0 : 0xffffffff) ^ ballot[wavePart];
                    }
                
                    uint bits = 0;
                    for (uint wavePart = 0; wavePart < waveParts; ++wavePart)
                    {
                        //%lanemask_le
                        if (WaveGetLaneIndex() >= wavePart * 32)
                        {
                            const uint leMask = WaveGetLaneIndex() >= ((wavePart + 1) * 32) - 1 ? 0xffffffff :
                            (1U << ((WaveGetLaneIndex() & 31) + 1)) - 1;
                            bits += countbits(waveFlags[wavePart] & leMask);
                        }
                    }
                    
                    const uint index = (keys[i] >> e_radixShift & RADIX_MASK) + (WAVE_INDEX * RADIX);
                    offsets[i] = g_dsMem[index] + bits - 1;
                    
                    GroupMemoryBarrierWithGroupSync();
                    if (bits == 1)
                    {
                        for (uint wavePart = 0; wavePart < waveParts; ++wavePart)
                            g_dsMem[index] += countbits(waveFlags[wavePart]);
                    }
                    GroupMemoryBarrierWithGroupSync();
                }
            }
            
            //inclusive/exclusive prefix sum up the histograms
            //followed by exclusive prefix sum across the reductions
            {
                uint reduction;
                if (gtid.x < RADIX)
                {
                    reduction = g_dsMem[gtid.x];
                    for (uint i = gtid.x + RADIX; i < DS_WGE16_HISTS_SIZE; i += RADIX)
                    {
                        reduction += g_dsMem[i];
                        g_dsMem[i] = reduction - g_dsMem[i];
                    }
                    reduction += WavePrefixSum(reduction);
                }
                GroupMemoryBarrierWithGroupSync();

                if (gtid.x < RADIX)
                {
                    const uint laneMask = WaveGetLaneCount() - 1;
                    g_dsMem[((WaveGetLaneIndex() + 1) & laneMask) + (gtid.x & ~laneMask)] = reduction;
                }
                GroupMemoryBarrierWithGroupSync();
                
                if (gtid.x < RADIX / WaveGetLaneCount())
                    g_dsMem[gtid.x * WaveGetLaneCount()] = WavePrefixSum(g_dsMem[gtid.x * WaveGetLaneCount()]);
                GroupMemoryBarrierWithGroupSync();
                
                if (gtid.x < RADIX && WaveGetLaneIndex())
                    g_dsMem[gtid.x] += WaveReadLaneAt(g_dsMem[gtid.x - 1], 1);
            }
            GroupMemoryBarrierWithGroupSync();
        
            //Update offsets
            if (gtid.x >= WaveGetLaneCount())
            {
                const uint t = WAVE_INDEX * RADIX;
                [unroll]
                for (uint i = 0; i < DS_KEYS_PER_THREAD; ++i)
                {
                    const uint t2 = keys[i] >> e_radixShift & RADIX_MASK;
                    offsets[i] += g_dsMem[t2 + t] + g_dsMem[t2];
                }
            }
            else
            {
                [unroll]
                for (uint i = 0; i < DS_KEYS_PER_THREAD; ++i)
                    offsets[i] += g_dsMem[keys[i] >> e_radixShift & RADIX_MASK];
            }
            
            //take advantage of barrier
            exclusiveWaveReduction = g_dsMem[gtid.x];
            GroupMemoryBarrierWithGroupSync();
            
            //scatter keys into shared memory
            for (uint i = 0; i < DS_KEYS_PER_THREAD; ++i)
                g_dsMem[offsets[i]] = keys[i];
        
            if (gtid.x < RADIX)
                g_dsMem[gtid.x + PARTITION_SIZE] = b_globalHist[gtid.x + (e_radixShift << 5)] + b_passHist[gtid.x * e_threadBlocks + gid.x] - exclusiveWaveReduction;
            GroupMemoryBarrierWithGroupSync();
        
            //scatter runs of keys into device memory, 
            //store the scatter location in the key register to reuse for the payload
            [unroll]
            for (uint i = 0, t = WaveGetLaneIndex() + DS_WGE16_PART_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount())
            {
                keys[i] = g_dsMem[(g_dsMem[t] >> e_radixShift & RADIX_MASK) + PARTITION_SIZE] + t;
                b_alt[keys[i]] = g_dsMem[t];
            }
            GroupMemoryBarrierWithGroupSync();
        
            //Scatter payloads directly into shared memory
            [unroll]
            for (uint i = 0, t = WaveGetLaneIndex() + DS_WGE16_PART_START + PARTITION_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount())
                g_dsMem[offsets[i]] = b_sortPayload[t];
            GroupMemoryBarrierWithGroupSync();
        
            //Scatter runs of payloads into device memory
            [unroll]
            for (uint i = 0, t = WaveGetLaneIndex() + DS_WGE16_PART_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount())
                b_altPayload[keys[i]] = g_dsMem[t];
        }
        
        if (WaveGetLaneCount() < 16)
        {
            const uint serialIterations = (DS_THREADS / WaveGetLaneCount() + 31) / 32;
            
            //Load keys into registers
            [unroll]
            for (uint i = 0, t = WaveGetLaneIndex() + DS_WLT16_PART_START + PARTITION_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount() * serialIterations)
                keys[i] = b_sort[t];
            
            //clear shared memory, max histogram size is divided by two because we are bitpacking
            for (uint i = gtid.x; i < DS_WLT16_HISTS_SIZE; i += DS_THREADS)
                g_dsMem[i] = 0;
            GroupMemoryBarrierWithGroupSync();
            
            {
                uint waveFlag;
                uint leMask = (1U << (WaveGetLaneIndex() + 1)) - 1; //for full agnostic add ternary
            
                [unroll]
                for (uint i = 0; i < DS_KEYS_PER_THREAD; ++i)
                {
                    waveFlag = (1U << WaveGetLaneCount()) - 1; //for full agnostic add ternary and uint4
                
                    for (uint k = e_radixShift; k < e_radixShift + RADIX_LOG; ++k)
                    {
                        const bool t = keys[i] >> k & 1;
                        waveFlag &= (t ? 0 : 0xffffffff) ^ (uint) WaveActiveBallot(t);
                    }
                
                    uint bits = countbits(waveFlag & leMask);
                    const uint index = (keys[i] >> (e_radixShift + 1) & HALF_MASK) + (WAVE_INDEX / serialIterations * HALF_RADIX);
                    
                    for (uint k = 0; k < serialIterations; ++k)
                    {
                        if ((WAVE_INDEX % serialIterations) == k)
                            offsets[i] = (g_dsMem[index] >> ((keys[i] >> e_radixShift & 1) ? 16 : 0) & 0xffff) + bits - 1;
                    
                        GroupMemoryBarrierWithGroupSync();
                        if ((WAVE_INDEX % serialIterations) == k && bits == 1)
                            InterlockedAdd(g_dsMem[index], countbits(waveFlag) << ((keys[i] >> e_radixShift & 1) ? 16 : 0));
                        GroupMemoryBarrierWithGroupSync();
                    }
                }
            }
            
            //inclusive/exclusive prefix sum up the histograms,
            //use a blelloch scan for in place exclusive
            {
                uint reduction;
                if (gtid.x < HALF_RADIX)
                {
                    reduction = g_dsMem[gtid.x];
                    for (uint i = gtid.x + HALF_RADIX; i < DS_WLT16_HISTS_SIZE; i += HALF_RADIX)
                    {
                        reduction += g_dsMem[i];
                        g_dsMem[i] = reduction - g_dsMem[i];
                    }
                    g_dsMem[gtid.x] = reduction + (reduction << 16);
                }
                
                uint shift = 1;
                for (uint j = RADIX >> 2; j > 0; j >>= 1)
                {
                    GroupMemoryBarrierWithGroupSync();
                    for (int i = gtid.x; i < j; i += UPSWEEP_THREADS)
                        g_dsMem[((((i << 1) + 2) << shift) - 1) >> 1] += g_dsMem[((((i << 1) + 1) << shift) - 1) >> 1] & 0xffff0000;
                    shift++;
                }
                GroupMemoryBarrierWithGroupSync();
                
                if (gtid.x == 0)
                    g_dsMem[HALF_RADIX - 1] &= 0xffff;
                
                for (uint j = 1; j < RADIX >> 1; j <<= 1)
                {
                    --shift;
                    GroupMemoryBarrierWithGroupSync();
                    for (uint i = gtid.x; i < j; i += UPSWEEP_THREADS)
                    {
                        const uint t = ((((i << 1) + 1) << shift) - 1) >> 1;
                        const uint t2 = ((((i << 1) + 2) << shift) - 1) >> 1;
                        const uint t3 = g_dsMem[t];
                        g_dsMem[t] = (g_dsMem[t] & 0xffff) | (g_dsMem[t2] & 0xffff0000);
                        g_dsMem[t2] += t3 & 0xffff0000;
                    }
                }

                GroupMemoryBarrierWithGroupSync();
                if (gtid.x < HALF_RADIX)
                {
                    const uint t = g_dsMem[gtid.x];
                    g_dsMem[gtid.x] = (t >> 16) + (t << 16) + (t & 0xffff0000);
                }
                GroupMemoryBarrierWithGroupSync();
            }
            
            //Update offsets
            if (gtid.x >= WaveGetLaneCount() * serialIterations)
            {
                const uint t = WAVE_INDEX / serialIterations * HALF_RADIX;
                [unroll]
                for (uint i = 0; i < DS_KEYS_PER_THREAD; ++i)
                {
                    const uint t2 = keys[i] >> (e_radixShift + 1) & HALF_MASK;
                    offsets[i] += (g_dsMem[t2 + t] + g_dsMem[t2]) >> ((keys[i] >> e_radixShift & 1) ? 16 : 0) & 0xffff;
                }
            }
            else
            {
                [unroll]
                for (uint i = 0; i < DS_KEYS_PER_THREAD; ++i)
                    offsets[i] += g_dsMem[keys[i] >> (e_radixShift + 1) & HALF_MASK] >> ((keys[i] >> e_radixShift & 1) ? 16 : 0) & 0xffff;
            }
            
            if (gtid.x < RADIX)
                exclusiveWaveReduction = g_dsMem[gtid.x >> 1] >> ((gtid.x & 1) ? 16 : 0) & 0xffff;
            GroupMemoryBarrierWithGroupSync();
            
            //scatter keys into shared memory
            for (uint i = 0; i < DS_KEYS_PER_THREAD; ++i)
                g_dsMem[offsets[i]] = keys[i];
        
            if (gtid.x < RADIX)
                g_dsMem[gtid.x + PARTITION_SIZE] = b_globalHist[gtid.x + (e_radixShift << 5)] + b_passHist[gtid.x * e_threadBlocks + gid.x] - exclusiveWaveReduction;
            GroupMemoryBarrierWithGroupSync();
        
            //scatter runs of keys into device memory, 
            //store the scatter location in the key register to reuse for the payload
            [unroll]
            for (uint i = 0, t = WaveGetLaneIndex() + DS_WLT16_PART_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount() * serialIterations)
            {
                keys[i] = g_dsMem[(g_dsMem[t] >> e_radixShift & RADIX_MASK) + PARTITION_SIZE] + t;
                b_alt[keys[i]] = g_dsMem[t];
            }
            GroupMemoryBarrierWithGroupSync();
        
            //Scatter payloads directly into shared memory
            [unroll]
            for (uint i = 0, t = WaveGetLaneIndex() + DS_WLT16_PART_START + PARTITION_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount() * serialIterations)
                g_dsMem[offsets[i]] = b_sortPayload[t];
            GroupMemoryBarrierWithGroupSync();
        
            //Scatter runs of payloads into device memory
            [unroll]
            for (uint i = 0, t = WaveGetLaneIndex() + DS_WLT16_PART_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount() * serialIterations)
                b_altPayload[keys[i]] = g_dsMem[t];
        }
    }
    
    //perform the sort on the final partition slightly differently 
    //to handle input sizes not perfect multiples of the partition
    if (gid.x == e_threadBlocks - 1)
    {
        //load the global and pass histogram values into shared memory
        if (gtid.x < RADIX)
            g_dsMem[gtid.x] = b_globalHist[gtid.x + (e_radixShift << 5)] + b_passHist[gtid.x * e_threadBlocks + gid.x];
        GroupMemoryBarrierWithGroupSync();
        
        const uint waveParts = (WaveGetLaneCount() + 31) / 32;
        for (int i = gtid.x + PARTITION_START; i < PARTITION_START + PARTITION_SIZE; i += DS_THREADS)
        {
            uint key;
            if (i < e_numKeys)
                key = b_sort[i];
            
            uint4 waveFlags = (WaveGetLaneCount() & 31) ? (1U << WaveGetLaneCount()) - 1 : 0xffffffff;
            uint offset;
            uint bits = 0;
            
            if (i < e_numKeys)
            {
                for (int k = e_radixShift; k < e_radixShift + RADIX_LOG; ++k)
                {
                    const bool t = key >> k & 1;
                    const uint4 ballot = WaveActiveBallot(t);
                    for (int wavePart = 0; wavePart < waveParts; ++wavePart)
                        waveFlags[wavePart] &= (t ? 0 : 0xffffffff) ^ ballot[wavePart];
                }
            
                for (int wavePart = 0; wavePart < waveParts; ++wavePart)
                {
                    if (WaveGetLaneIndex() >= wavePart * 32)
                    {
                        const uint leMask = WaveGetLaneIndex() >= ((wavePart + 1) * 32) - 1 ? 0xffffffff :
                            (1U << ((WaveGetLaneIndex() & 31) + 1)) - 1;
                        bits += countbits(waveFlags[wavePart] & leMask);
                    }
                }
            }
            
            for (int k = 0; k < DS_THREADS / WaveGetLaneCount(); ++k)
            {
                if (WAVE_INDEX == k && i < e_numKeys)
                    offset = g_dsMem[key >> e_radixShift & RADIX_MASK] + bits - 1;
                GroupMemoryBarrierWithGroupSync();
                
                if (WAVE_INDEX == k && i < e_numKeys && bits == 1)
                {
                    for (int wavePart = 0; wavePart < waveParts; ++wavePart)
                        g_dsMem[key >> e_radixShift & RADIX_MASK] += countbits(waveFlags[wavePart]);
                }
                GroupMemoryBarrierWithGroupSync();
            }

            if (i < e_numKeys)
            {
                b_alt[offset] = key;
                b_altPayload[offset] = b_sortPayload[i];
            }
        }
    }
}