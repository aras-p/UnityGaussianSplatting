/******************************************************************************
 * DeviceRadixSort
 * Device Level 8-bit LSD Radix Sort using reduce then scan
 * 
 * SPDX-License-Identifier: MIT
 * Copyright Thomas Smith 5/17/2024
 * https://github.com/b0nes164/GPUSorting
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
#include "SortCommon.hlsl"

#define US_DIM          128U        //The number of threads in a Upsweep threadblock
#define SCAN_DIM        128U        //The number of threads in a Scan threadblock

RWStructuredBuffer<uint> b_globalHist;  //buffer holding device level offsets for each binning pass
RWStructuredBuffer<uint> b_passHist;    //buffer used to store reduced sums of partition tiles

groupshared uint g_us[RADIX * 2];   //Shared memory for upsweep
groupshared uint g_scan[SCAN_DIM];  //Shared memory for the scan

//*****************************************************************************
//INIT KERNEL
//*****************************************************************************
//Clear the global histogram, as we will be adding to it atomically
[numthreads(1024, 1, 1)]
void InitDeviceRadixSort(int3 id : SV_DispatchThreadID)
{
    b_globalHist[id.x] = 0;
}

//*****************************************************************************
//UPSWEEP KERNEL
//*****************************************************************************
//histogram, 64 threads to a histogram
inline void HistogramDigitCounts(uint gtid, uint gid)
{
    const uint histOffset = gtid / 64 * RADIX;
    const uint partitionEnd = gid == e_threadBlocks - 1 ?
        e_numKeys : (gid + 1) * PART_SIZE;
    for (uint i = gtid + gid * PART_SIZE; i < partitionEnd; i += US_DIM)
    {
#if defined(KEY_UINT)
        InterlockedAdd(g_us[ExtractDigit(b_sort[i]) + histOffset], 1);
#elif defined(KEY_INT)
        InterlockedAdd(g_us[ExtractDigit(IntToUint(b_sort[i])) + histOffset], 1);
#elif defined(KEY_FLOAT)
        InterlockedAdd(g_us[ExtractDigit(FloatToUint(b_sort[i])) + histOffset], 1);
#endif
    }
}

//reduce and pass to tile histogram
inline void ReduceWriteDigitCounts(uint gtid, uint gid)
{
    for (uint i = gtid; i < RADIX; i += US_DIM)
    {
        g_us[i] += g_us[i + RADIX];
        b_passHist[i * e_threadBlocks + gid] = g_us[i];
        g_us[i] += WavePrefixSum(g_us[i]);
    }
}

//Exclusive scan over digit counts, then atomically add to global hist
inline void GlobalHistExclusiveScanWGE16(uint gtid, uint waveSize)
{
    GroupMemoryBarrierWithGroupSync();
        
    if (gtid < (RADIX / waveSize))
    {
        g_us[(gtid + 1) * waveSize - 1] +=
            WavePrefixSum(g_us[(gtid + 1) * waveSize - 1]);
    }
    GroupMemoryBarrierWithGroupSync();
        
    //atomically add to global histogram
    const uint globalHistOffset = GlobalHistOffset();
    const uint laneMask = waveSize - 1;
    const uint circularLaneShift = WaveGetLaneIndex() + 1 & laneMask;
    for (uint i = gtid; i < RADIX; i += US_DIM)
    {
        const uint index = circularLaneShift + (i & ~laneMask);
        uint t = WaveGetLaneIndex() != laneMask ? g_us[i] : 0;
        if (i >= waveSize)
            t += WaveReadLaneAt(g_us[i - 1], 0);
        InterlockedAdd(b_globalHist[index + globalHistOffset], t);
    }
}

inline void GlobalHistExclusiveScanWLT16(uint gtid, uint waveSize)
{
    const uint globalHistOffset = GlobalHistOffset();
    if (gtid < waveSize)
    {
        const uint circularLaneShift = WaveGetLaneIndex() + 1 &
            waveSize - 1;
        InterlockedAdd(b_globalHist[circularLaneShift + globalHistOffset],
            circularLaneShift ? g_us[gtid] : 0);
    }
    GroupMemoryBarrierWithGroupSync();
        
    const uint laneLog = countbits(waveSize - 1);
    uint offset = laneLog;
    uint j = waveSize;
    for (; j < (RADIX >> 1); j <<= laneLog)
    {
        if (gtid < (RADIX >> offset))
        {
            g_us[((gtid + 1) << offset) - 1] +=
                WavePrefixSum(g_us[((gtid + 1) << offset) - 1]);
        }
        GroupMemoryBarrierWithGroupSync();
            
        for (uint i = gtid + j; i < RADIX; i += US_DIM)
        {
            if ((i & ((j << laneLog) - 1)) >= j)
            {
                if (i < (j << laneLog))
                {
                    InterlockedAdd(b_globalHist[i + globalHistOffset],
                        WaveReadLaneAt(g_us[((i >> offset) << offset) - 1], 0) +
                        ((i & (j - 1)) ? g_us[i - 1] : 0));
                }
                else
                {
                    if ((i + 1) & (j - 1))
                    {
                        g_us[i] +=
                            WaveReadLaneAt(g_us[((i >> offset) << offset) - 1], 0);
                    }
                }
            }
        }
        offset += laneLog;
    }
    GroupMemoryBarrierWithGroupSync();
        
    //If RADIX is not a power of lanecount
    for (uint i = gtid + j; i < RADIX; i += US_DIM)
    {
        InterlockedAdd(b_globalHist[i + globalHistOffset],
            WaveReadLaneAt(g_us[((i >> offset) << offset) - 1], 0) +
            ((i & (j - 1)) ? g_us[i - 1] : 0));
    }
}

[numthreads(US_DIM, 1, 1)]
void Upsweep(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    //get the wave size
    const uint waveSize = getWaveSize();
    
    //clear shared memory
    const uint histsEnd = RADIX * 2;
    for (uint i = gtid.x; i < histsEnd; i += US_DIM)
        g_us[i] = 0;
    GroupMemoryBarrierWithGroupSync();

    HistogramDigitCounts(gtid.x, gid.x);
    GroupMemoryBarrierWithGroupSync();
    
    ReduceWriteDigitCounts(gtid.x, gid.x);
    
    if (waveSize >= 16)
        GlobalHistExclusiveScanWGE16(gtid.x, waveSize);
    
    if (waveSize < 16)
        GlobalHistExclusiveScanWLT16(gtid.x, waveSize);
}

//*****************************************************************************
//SCAN KERNEL
//*****************************************************************************
inline void ExclusiveThreadBlockScanFullWGE16(
    uint gtid,
    uint laneMask,
    uint circularLaneShift,
    uint partEnd,
    uint deviceOffset,
    uint waveSize,
    inout uint reduction)
{
    for (uint i = gtid; i < partEnd; i += SCAN_DIM)
    {
        g_scan[gtid] = b_passHist[i + deviceOffset];
        g_scan[gtid] += WavePrefixSum(g_scan[gtid]);
        GroupMemoryBarrierWithGroupSync();
            
        if (gtid < SCAN_DIM / waveSize)
        {
            g_scan[(gtid + 1) * waveSize - 1] +=
                WavePrefixSum(g_scan[(gtid + 1) * waveSize - 1]);
        }
        GroupMemoryBarrierWithGroupSync();
        
        uint t = (WaveGetLaneIndex() != laneMask ? g_scan[gtid] : 0) + reduction;
        if (gtid >= waveSize)
            t += WaveReadLaneAt(g_scan[gtid - 1], 0);
        b_passHist[circularLaneShift + (i & ~laneMask) + deviceOffset] = t;

        reduction += g_scan[SCAN_DIM - 1];
        GroupMemoryBarrierWithGroupSync();
    }
}

inline void ExclusiveThreadBlockScanPartialWGE16(
    uint gtid,
    uint laneMask,
    uint circularLaneShift,
    uint partEnd,
    uint deviceOffset,
    uint waveSize,
    uint reduction)
{
    uint i = gtid + partEnd;
    if (i < e_threadBlocks)
        g_scan[gtid] = b_passHist[deviceOffset + i];
    g_scan[gtid] += WavePrefixSum(g_scan[gtid]);
    GroupMemoryBarrierWithGroupSync();
            
    if (gtid < SCAN_DIM / waveSize)
    {
        g_scan[(gtid + 1) * waveSize - 1] +=
            WavePrefixSum(g_scan[(gtid + 1) * waveSize - 1]);
    }
    GroupMemoryBarrierWithGroupSync();
        
    const uint index = circularLaneShift + (i & ~laneMask);
    if (index < e_threadBlocks)
    {
        uint t = (WaveGetLaneIndex() != laneMask ? g_scan[gtid] : 0) + reduction;
        if (gtid >= waveSize)
            t += g_scan[(gtid & ~laneMask) - 1];
        b_passHist[index + deviceOffset] = t;
    }
}

inline void ExclusiveThreadBlockScanWGE16(uint gtid, uint gid, uint waveSize)
{
    uint reduction = 0;
    const uint laneMask = waveSize - 1;
    const uint circularLaneShift = WaveGetLaneIndex() + 1 & laneMask;
    const uint partionsEnd = e_threadBlocks / SCAN_DIM * SCAN_DIM;
    const uint deviceOffset = gid * e_threadBlocks;
    
    ExclusiveThreadBlockScanFullWGE16(
        gtid,
        laneMask,
        circularLaneShift,
        partionsEnd,
        deviceOffset,
        waveSize,
        reduction);

    ExclusiveThreadBlockScanPartialWGE16(
        gtid,
        laneMask,
        circularLaneShift,
        partionsEnd,
        deviceOffset,
        waveSize,
        reduction);
}

inline void ExclusiveThreadBlockScanFullWLT16(
    uint gtid,
    uint partitions,
    uint deviceOffset,
    uint laneLog,
    uint circularLaneShift,
    uint waveSize,
    inout uint reduction)
{
    for (uint k = 0; k < partitions; ++k)
    {
        g_scan[gtid] = b_passHist[gtid + k * SCAN_DIM + deviceOffset];
        g_scan[gtid] += WavePrefixSum(g_scan[gtid]);
        GroupMemoryBarrierWithGroupSync();
        if (gtid < waveSize)
        {
            b_passHist[circularLaneShift + k * SCAN_DIM + deviceOffset] =
                (circularLaneShift ? g_scan[gtid] : 0) + reduction;
        }
            
        uint offset = laneLog;
        uint j = waveSize;
        for (; j < (SCAN_DIM >> 1); j <<= laneLog)
        {
            if (gtid < (SCAN_DIM >> offset))
            {
                g_scan[((gtid + 1) << offset) - 1] +=
                    WavePrefixSum(g_scan[((gtid + 1) << offset) - 1]);
            }
            GroupMemoryBarrierWithGroupSync();
            
            if ((gtid & ((j << laneLog) - 1)) >= j)
            {
                if (gtid < (j << laneLog))
                {
                    b_passHist[gtid + k * SCAN_DIM + deviceOffset] =
                        WaveReadLaneAt(g_scan[((gtid >> offset) << offset) - 1], 0) +
                        ((gtid & (j - 1)) ? g_scan[gtid - 1] : 0) + reduction;
                }
                else
                {
                    if ((gtid + 1) & (j - 1))
                    {
                        g_scan[gtid] +=
                            WaveReadLaneAt(g_scan[((gtid >> offset) << offset) - 1], 0);
                    }
                }
            }
            offset += laneLog;
        }
        GroupMemoryBarrierWithGroupSync();
        
        //If SCAN_DIM is not a power of lanecount
        for (uint i = gtid + j; i < SCAN_DIM; i += SCAN_DIM)
        {
            b_passHist[i + k * SCAN_DIM + deviceOffset] =
                WaveReadLaneAt(g_scan[((i >> offset) << offset) - 1], 0) +
                ((i & (j - 1)) ? g_scan[i - 1] : 0) + reduction;
        }
            
        reduction += WaveReadLaneAt(g_scan[SCAN_DIM - 1], 0) +
            WaveReadLaneAt(g_scan[(((SCAN_DIM - 1) >> offset) << offset) - 1], 0);
        GroupMemoryBarrierWithGroupSync();
    }
}

inline void ExclusiveThreadBlockScanParitalWLT16(
    uint gtid,
    uint partitions,
    uint deviceOffset,
    uint laneLog,
    uint circularLaneShift,
    uint waveSize,
    uint reduction)
{
    const uint finalPartSize = e_threadBlocks - partitions * SCAN_DIM;
    if (gtid < finalPartSize)
    {
        g_scan[gtid] = b_passHist[gtid + partitions * SCAN_DIM + deviceOffset];
        g_scan[gtid] += WavePrefixSum(g_scan[gtid]);
    }
    GroupMemoryBarrierWithGroupSync();
    if (gtid < waveSize && circularLaneShift < finalPartSize)
    {
        b_passHist[circularLaneShift + partitions * SCAN_DIM + deviceOffset] =
            (circularLaneShift ? g_scan[gtid] : 0) + reduction;
    }
        
    uint offset = laneLog;
    for (uint j = waveSize; j < finalPartSize; j <<= laneLog)
    {
        if (gtid < (finalPartSize >> offset))
        {
            g_scan[((gtid + 1) << offset) - 1] +=
                WavePrefixSum(g_scan[((gtid + 1) << offset) - 1]);
        }
        GroupMemoryBarrierWithGroupSync();
            
        if ((gtid & ((j << laneLog) - 1)) >= j && gtid < finalPartSize)
        {
            if (gtid < (j << laneLog))
            {
                b_passHist[gtid + partitions * SCAN_DIM + deviceOffset] =
                    WaveReadLaneAt(g_scan[((gtid >> offset) << offset) - 1], 0) +
                    ((gtid & (j - 1)) ? g_scan[gtid - 1] : 0) + reduction;
            }
            else
            {
                if ((gtid + 1) & (j - 1))
                {
                    g_scan[gtid] +=
                        WaveReadLaneAt(g_scan[((gtid >> offset) << offset) - 1], 0);
                }
            }
        }
        offset += laneLog;
    }
}

inline void ExclusiveThreadBlockScanWLT16(uint gtid, uint gid, uint waveSize)
{
    uint reduction = 0;
    const uint partitions = e_threadBlocks / SCAN_DIM;
    const uint deviceOffset = gid * e_threadBlocks;
    const uint laneLog = countbits(waveSize - 1);
    const uint circularLaneShift = WaveGetLaneIndex() + 1 & waveSize - 1;
    
    ExclusiveThreadBlockScanFullWLT16(
        gtid,
        partitions,
        deviceOffset,
        laneLog,
        circularLaneShift,
        waveSize,
        reduction);
    
    ExclusiveThreadBlockScanParitalWLT16(
        gtid,
        partitions,
        deviceOffset,
        laneLog,
        circularLaneShift,
        waveSize,
        reduction);
}

//Scan does not need flattening of gids
[numthreads(SCAN_DIM, 1, 1)]
void Scan(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    const uint waveSize = getWaveSize();
    if (waveSize >= 16)
        ExclusiveThreadBlockScanWGE16(gtid.x, gid.x, waveSize);

    if (waveSize < 16)
        ExclusiveThreadBlockScanWLT16(gtid.x, gid.x, waveSize);
}

//*****************************************************************************
//DOWNSWEEP KERNEL
//*****************************************************************************
inline void LoadThreadBlockReductions(uint gtid, uint gid, uint exclusiveHistReduction)
{
    if (gtid < RADIX)
    {
        g_d[gtid + PART_SIZE] = b_globalHist[gtid + GlobalHistOffset()] +
            b_passHist[gtid * e_threadBlocks + gid] - exclusiveHistReduction;
    }
}

[numthreads(D_DIM, 1, 1)]
void Downsweep(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    KeyStruct keys;
    OffsetStruct offsets;
    const uint waveSize = getWaveSize();
    
    ClearWaveHists(gtid.x, waveSize);
    GroupMemoryBarrierWithGroupSync();
    
    if (gid.x < e_threadBlocks - 1)
    {
        if (waveSize >= 16)
            keys = LoadKeysWGE16(gtid.x, waveSize, gid.x);
        
        if (waveSize < 16)
            keys = LoadKeysWLT16(gtid.x, waveSize, gid.x, SerialIterations(waveSize));
    }
        
    if (gid.x == e_threadBlocks - 1)
    {
        if (waveSize >= 16)
            keys = LoadKeysPartialWGE16(gtid.x, waveSize, gid.x);
        
        if (waveSize < 16)
            keys = LoadKeysPartialWLT16(gtid.x, waveSize, gid.x, SerialIterations(waveSize));
    }
    
    uint exclusiveHistReduction;
    
    if (waveSize >= 16)
    {
        offsets = RankKeysWGE16(waveSize, getWaveIndex(gtid.x, waveSize) * RADIX, keys);
        GroupMemoryBarrierWithGroupSync();
        
        uint histReduction;
        if (gtid.x < RADIX)
        {
            histReduction = WaveHistInclusiveScanCircularShiftWGE16(gtid.x, waveSize);
            histReduction += WavePrefixSum(histReduction); //take advantage of barrier to begin scan
        }
        GroupMemoryBarrierWithGroupSync();
        
        WaveHistReductionExclusiveScanWGE16(gtid.x, waveSize, histReduction);
        GroupMemoryBarrierWithGroupSync();
            
        UpdateOffsetsWGE16(gtid.x, waveSize, offsets, keys);
        if (gtid.x < RADIX)
            exclusiveHistReduction = g_d[gtid.x]; //take advantage of barrier to grab value
        GroupMemoryBarrierWithGroupSync();
    }
    
    if (waveSize < 16)
    {
        offsets = RankKeysWLT16(waveSize, getWaveIndex(gtid.x, waveSize), keys, SerialIterations(waveSize));
            
        if (gtid.x < HALF_RADIX)
        {
            uint histReduction = WaveHistInclusiveScanCircularShiftWLT16(gtid.x);
            g_d[gtid.x] = histReduction + (histReduction << 16); //take advantage of barrier to begin scan
        }
            
        WaveHistReductionExclusiveScanWLT16(gtid.x);
        GroupMemoryBarrierWithGroupSync();
            
        UpdateOffsetsWLT16(gtid.x, waveSize, SerialIterations(waveSize), offsets, keys);
        if (gtid.x < RADIX) //take advantage of barrier to grab value
            exclusiveHistReduction = g_d[gtid.x >> 1] >> ((gtid.x & 1) ? 16 : 0) & 0xffff;
        GroupMemoryBarrierWithGroupSync();
    }
    
    ScatterKeysShared(offsets, keys);
    LoadThreadBlockReductions(gtid.x, gid.x, exclusiveHistReduction);
    GroupMemoryBarrierWithGroupSync();
    
    if (gid.x < e_threadBlocks - 1)
        ScatterDevice(gtid.x, waveSize, gid.x, offsets);
        
    if (gid.x == e_threadBlocks - 1)
        ScatterDevicePartial(gtid.x, waveSize, gid.x, offsets);
}