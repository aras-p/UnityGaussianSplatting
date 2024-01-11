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
#define PARTITION_SIZE      7680    //size of a partition tile

#define RADIX               256     //Number of digit bins
#define RADIX_MASK          255     //Mask of digit bins
#define RADIX_LOG           8       //log2(RADIX)

#define WAVE_INDEX          (gtid.x / WaveGetLaneCount())
#define PARTITION_START     (gid.x * PARTITION_SIZE)

#define UPSWEEP_THREADS     64      //The number of threads in a Upsweep threadblock
#define SCAN_THREADS        64      //The number of threads in a Scan threadblock
#define DS_THREADS          512     //The number of threads in a Downsweep threadblock

//For the downsweep kernels
#define DS_KEYS_PER_THREAD  15                                          //The number of keys per thread in BinningPass threadblock
#define DS_WAVE_PART_SIZE   480                                         //The subpartition size of a single wave in a BinningPass threadblock
#define DS_WAVE_PART_START  (WAVE_INDEX * DS_WAVE_PART_SIZE)            //The starting offset of a subpartition tile
#define DS_HISTS_SIZE       (DS_THREADS / WaveGetLaneCount() * RADIX)   //The total size of all wave histograms in shared memory

//needs to match original cBuffer size?
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

RWStructuredBuffer<uint> b_polling;     //buffer used to grab the wave size during intialization
RWStructuredBuffer<uint> b_sort;        //buffer to be sorted
RWStructuredBuffer<uint> b_sortPayload; //payload buffer
RWStructuredBuffer<uint> b_alt;         //double buffer
RWStructuredBuffer<uint> b_altPayload;  //double buffer payload
RWStructuredBuffer<uint> b_globalHist;  //buffer holding device level offsets for each binning pass
RWStructuredBuffer<uint> b_passHist;    //buffer used to store reduced sums of partition tiles

groupshared uint g_upsweepHist[RADIX];  //Shared memory for upsweep
groupshared uint g_localHist[RADIX];    //Threadgroup copy of globalHist during downsweep
groupshared uint g_waveHists[PARTITION_SIZE];   //Shared memory for the per wave histograms during downsweep

//Clear the global histogram, as we will be adding to it atomically
[numthreads(1024, 1, 1)]
void InitDeviceRadixSort(int3 id : SV_DispatchThreadID)
{
    b_globalHist[id.x] = 0;
}

//Ask the GPU how many lanes are in a wave
[numthreads(1, 1, 1)]
void PollWaveSize(int3 gtid : SV_GroupThreadID)
{
    b_polling[0] = WaveGetLaneCount();
}

[numthreads(UPSWEEP_THREADS, 1, 1)]
void Upsweep(int3 gtid : SV_GroupThreadID, int3 gid : SV_GroupID)
{
    //clear shared memory
    for (int i = gtid.x; i < RADIX; i += UPSWEEP_THREADS)
        g_upsweepHist[i] = 0;
    GroupMemoryBarrierWithGroupSync();

    //histogram
    {
        const int radixShift = e_radixShift;
        const int partitionEnd = gid.x == e_threadBlocks - 1 ? e_numKeys : (gid.x + 1) * PARTITION_SIZE;
        for (int i = gtid.x + PARTITION_START; i < partitionEnd; i += UPSWEEP_THREADS)
            InterlockedAdd(g_upsweepHist[b_sort[i] >> radixShift & RADIX_MASK], 1);
    }
    GroupMemoryBarrierWithGroupSync();
    
    //pass tile to pass histogram
    {
        const int threadBlocks = e_threadBlocks;
        for (int i = gtid.x; i < RADIX; i += UPSWEEP_THREADS)
            b_passHist[i * threadBlocks + gid.x] = g_upsweepHist[i];
    }
    GroupMemoryBarrierWithGroupSync();
    
    //exclusive prefix sum over the counts
    {
        const int laneMask = WaveGetLaneCount() - 1;
        const int circularLaneShift = WaveGetLaneIndex() + 1 & laneMask;
        for (int i = gtid.x; i < RADIX; i += UPSWEEP_THREADS)
            g_upsweepHist[circularLaneShift + (i & ~laneMask)] = g_upsweepHist[i] + WavePrefixSum(g_upsweepHist[i]);
    }
    GroupMemoryBarrierWithGroupSync();
    
    if (WaveGetLaneIndex() < (RADIX / WaveGetLaneCount()) && WAVE_INDEX == 0)
        g_upsweepHist[WaveGetLaneIndex() * WaveGetLaneCount()] = WavePrefixSum(g_upsweepHist[WaveGetLaneIndex() * WaveGetLaneCount()]);
    GroupMemoryBarrierWithGroupSync();
    
    //atomically add to global histogram
    {
        const int offset = e_radixShift << 5;
        for (int i = gtid.x; i < RADIX; i += UPSWEEP_THREADS)
            InterlockedAdd(b_globalHist[i + offset], (WaveGetLaneIndex() ? g_upsweepHist[i] : 0) + WaveReadLaneAt(g_upsweepHist[i], 0));
    }
}

//Scan along the spine of the upsweep
[numthreads(SCAN_THREADS, 1, 1)]
void Scan(int3 gtid : SV_GroupThreadID, int3 gid : SV_GroupID)
{
    uint aggregate = 0;
    const int threadBlocks = e_threadBlocks;
    const int tBlocksEnd = (threadBlocks + WaveGetLaneCount() - 1) / WaveGetLaneCount() * WaveGetLaneCount();
    const int laneMask = WaveGetLaneCount() - 1;
    const int circularLaneShift = WaveGetLaneIndex() + 1 & laneMask;
    const int offset = (gid.x * (SCAN_THREADS / WaveGetLaneCount()) + WAVE_INDEX) * threadBlocks;
    
    for (int i = WaveGetLaneIndex(); i < tBlocksEnd; i += WaveGetLaneCount())
    {
        uint t;
        if (i < threadBlocks)
            t = b_passHist[i + offset];
        t += WavePrefixSum(t);
        const int index = circularLaneShift + (i & ~laneMask);
        if (index < threadBlocks)
            b_passHist[index + offset] = (WaveGetLaneIndex() != laneMask ? t : 0) + aggregate;
        aggregate += WaveReadLaneAt(t, laneMask);
    }
}

[numthreads(DS_THREADS, 1, 1)]
void Downsweep(int3 gtid : SV_GroupThreadID, int3 gid : SV_GroupID)
{
    //load the global and pass histogram values into shared memory
    if (gtid.x < RADIX)
        g_localHist[gtid.x] = b_globalHist[gtid.x + (e_radixShift << 5)] + b_passHist[gtid.x * e_threadBlocks + gid.x];
    
    //Each wave clears its own portion of shared memory
    {
        const int waveHistEnd = (WAVE_INDEX + 1) * RADIX;
        for (int i = WaveGetLaneIndex() + WAVE_INDEX * RADIX; i < waveHistEnd; i += WaveGetLaneCount())
            g_waveHists[i] = 0;
    }
    GroupMemoryBarrierWithGroupSync();
    
    if (gid.x != e_threadBlocks - 1)
    {
        //Load keys into registers
        uint keys[DS_KEYS_PER_THREAD];
        [unroll]
        for (int i = 0, t = WaveGetLaneIndex() + DS_WAVE_PART_START + PARTITION_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount())
            keys[i] = b_sort[t];

        //Warp Level Multisplit, should support SIMD width up to 128
        uint offsets[DS_KEYS_PER_THREAD];
        {
            const uint waveParts = WaveGetLaneCount() / 32;
            uint4 waveFlags;
            
            [unroll]
            for (int i = 0; i < DS_KEYS_PER_THREAD; ++i)
            {
                waveFlags = 0xffffffff;

                for (int k = e_radixShift; k < e_radixShift + RADIX_LOG; ++k)
                {
                    const bool t = keys[i] >> k & 1;
                    const uint4 ballot = WaveActiveBallot(t);
                    for (int wavePart = 0; wavePart < waveParts; ++wavePart)
                        waveFlags[wavePart] &= (t ? 0 : 0xffffffff) ^ ballot[wavePart];
                }
                
                uint bits = 0;
                for (int wavePart = 0; wavePart < waveParts; ++wavePart)
                {
                    //%lanemask_le, but gross
                    if (WaveGetLaneIndex() >= (wavePart << 5))
                    {
                        const uint leMask = WaveGetLaneIndex() >= ((wavePart + 1) << 5) - 1 ? 0xffffffff :
                            (1 << (WaveGetLaneIndex() & 31) + 1) - 1;
                        bits += countbits(waveFlags[wavePart] & leMask);
                    }
                }
                    
                const int index = (keys[i] >> e_radixShift & RADIX_MASK) + (WAVE_INDEX * RADIX);
                offsets[i] = g_waveHists[index] + bits - 1;
                if (bits == 1)
                {
                    for (int wavePart = 0; wavePart < waveParts; ++wavePart)
                        g_waveHists[index] += countbits(waveFlags[wavePart]);
                }
                GroupMemoryBarrierWithGroupSync();
            }
        }
        
        //exclusive prefix sum across the histograms
        if (gtid.x < RADIX)
        {
            for (int k = gtid.x + RADIX; k < DS_HISTS_SIZE; k += RADIX)
            {
                g_waveHists[gtid.x] += g_waveHists[k];
                g_waveHists[k] = g_waveHists[gtid.x] - g_waveHists[k];
            }
        }
        GroupMemoryBarrierWithGroupSync();

        //exclusive prefix sum across the reductions
        {
            const uint t = (WaveGetLaneIndex() + 1 & WaveGetLaneCount() - 1) + (WAVE_INDEX * WaveGetLaneCount());
            if (gtid.x < RADIX)
                g_waveHists[t] = WavePrefixSum(g_waveHists[gtid.x]) + g_waveHists[gtid.x];
            GroupMemoryBarrierWithGroupSync();
        
            if (WaveGetLaneIndex() < (RADIX / WaveGetLaneCount()) && WAVE_INDEX == 0)
                g_waveHists[WaveGetLaneIndex() * WaveGetLaneCount()] =
                WavePrefixSum(g_waveHists[WaveGetLaneIndex() * WaveGetLaneCount()]);
            GroupMemoryBarrierWithGroupSync();

            if (gtid.x < RADIX)
            {
                if (WaveGetLaneIndex())
                    g_waveHists[gtid.x] += WaveReadLaneAt(g_waveHists[gtid.x - 1], 1);
                g_localHist[gtid.x] -= g_waveHists[gtid.x];
            }
        }
        GroupMemoryBarrierWithGroupSync();
        
        //Update offsets
        if (gtid.x >= WaveGetLaneCount())
        {
            const uint t = WAVE_INDEX * RADIX;
            [unroll]
            for (int i = 0; i < DS_KEYS_PER_THREAD; ++i)
            {
                const uint t2 = keys[i] >> e_radixShift & RADIX_MASK;
                offsets[i] += g_waveHists[t2 + t] + g_waveHists[t2];
            }
        }
        else
        {
            [unroll]
            for (int i = 0; i < DS_KEYS_PER_THREAD; ++i)
                offsets[i] += g_waveHists[keys[i] >> e_radixShift & RADIX_MASK];
        }
        GroupMemoryBarrierWithGroupSync();
        
        //scatter keys into shared memory
        for (int i = 0; i < DS_KEYS_PER_THREAD; ++i)
            g_waveHists[offsets[i]] = keys[i];
        GroupMemoryBarrierWithGroupSync();
        
        //scatter runs of keys into device memory, 
        //store the scatter location in the key register to reuse for the payload
        [unroll]
        for (int i = 0, t = WaveGetLaneIndex() + DS_WAVE_PART_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount())
        {
            keys[i] = g_localHist[g_waveHists[t] >> e_radixShift & RADIX_MASK] + t;
            b_alt[keys[i]] = g_waveHists[t];
        }
        GroupMemoryBarrierWithGroupSync();
        
        //Scatter payloads directly into shared memory
        [unroll]
        for (int i = 0, t = WaveGetLaneIndex() + DS_WAVE_PART_START + PARTITION_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount())
            g_waveHists[offsets[i]] = b_sortPayload[t];
        GroupMemoryBarrierWithGroupSync();
        
        //Scatter runs of payloads into device memory
        [unroll]
        for (int i = 0, t = WaveGetLaneIndex() + DS_WAVE_PART_START; i < DS_KEYS_PER_THREAD; ++i, t += WaveGetLaneCount())
            b_altPayload[keys[i]] = g_waveHists[t];
    }
    else
    {
        //perform the sort on the final partition slightly differently 
        //to handle input sizes not perfect multiples of the partition
        const uint waveParts = WaveGetLaneCount() / 32;
        for (int i = gtid.x + PARTITION_START; i < PARTITION_START + PARTITION_SIZE; i += DS_THREADS)
        {
            const uint key = b_sort[i];
            uint4 waveFlags = 0xffffffff;
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
                            (1 << (WaveGetLaneIndex() & 31) + 1) - 1;
                        bits += countbits(waveFlags[wavePart] & leMask);
                    }
                }
            }
            
            for (int k = 0; k < DS_THREADS / WaveGetLaneCount(); ++k)
            {
                if (WAVE_INDEX == k && i < e_numKeys)
                {
                    offset = g_localHist[key >> e_radixShift & RADIX_MASK] + bits - 1;
                    if (bits == 1)
                    {
                        for (int wavePart = 0; wavePart < waveParts; ++wavePart)
                            g_localHist[key >> e_radixShift & RADIX_MASK] += countbits(waveFlags[wavePart]);
                    }
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