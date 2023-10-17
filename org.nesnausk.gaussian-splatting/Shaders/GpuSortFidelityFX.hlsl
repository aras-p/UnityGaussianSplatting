// Originally based on FidelityFX SDK, Copyright © 2023 Advanced Micro Devices, Inc., MIT license
// https://github.com/GPUOpen-Effects/FidelityFX-ParallelSort v1.1.1

// -------- Constant buffer data

cbuffer cbParallelSort : register(b0)
{
    uint numKeys;
    int  numBlocksPerThreadGroup;
    uint numThreadGroups;
    uint numThreadGroupsWithAdditionalBlocks;
    uint numReduceThreadgroupPerBin;
    uint numScanValues;
    uint shift;
    uint padding;
};

uint FfxNumKeys() { return numKeys; }
int FfxNumBlocksPerThreadGroup() { return numBlocksPerThreadGroup; }
uint FfxNumThreadGroups() { return numThreadGroups; }
uint FfxNumThreadGroupsWithAdditionalBlocks() { return numThreadGroupsWithAdditionalBlocks; }
uint FfxNumReduceThreadgroupPerBin() { return numReduceThreadgroupPerBin; }
uint FfxNumScanValues() { return numScanValues; }
uint FfxShiftBit() { return shift; }

// -------- Read/Write buffers

RWStructuredBuffer<uint> rw_source_keys;
RWStructuredBuffer<uint> rw_dest_keys;
RWStructuredBuffer<uint> rw_source_payloads;
RWStructuredBuffer<uint> rw_dest_payloads;
RWStructuredBuffer<uint> rw_sum_table;
RWStructuredBuffer<uint> rw_reduce_table;
RWStructuredBuffer<uint> rw_scan_source;
RWStructuredBuffer<uint> rw_scan_dest;
RWStructuredBuffer<uint> rw_scan_scratch;

uint FfxLoadKey(uint index)
{
    return rw_source_keys[index];
}
void FfxStoreKey(uint index, uint value)
{
    rw_dest_keys[index] = value;
}
uint FfxLoadPayload(uint index)
{
    return rw_source_payloads[index];
}
void FfxStorePayload(uint index, uint value)
{
    rw_dest_payloads[index] = value;
}
uint FfxLoadSum(uint index)
{
    return rw_sum_table[index];
}
void FfxStoreSum(uint index, uint value)
{
    rw_sum_table[index] = value;
}
void FfxStoreReduce(uint index, uint value)
{
    rw_reduce_table[index] = value;
}
uint FfxLoadScanSource(uint index)
{
    return rw_scan_source[index];
}
void FfxStoreScanDest(uint index, uint value)
{
    rw_scan_dest[index] = value;
}
uint FfxLoadScanScratch(uint index)
{
    return rw_scan_scratch[index];
}

// -------- Compile-time constants

// The number of bits we are sorting per pass. Changing this value requires internal changes in LDS distribution
// and count, reduce, scan, and scatter passes
#define FFX_PARALLELSORT_SORT_BITS_PER_PASS		    4

// The number of bins used for the counting phase of the algorithm. Changing this value requires internal
// changes in LDS distribution and count, reduce, scan, and scatter passes
#define	FFX_PARALLELSORT_SORT_BIN_COUNT			    (1 << FFX_PARALLELSORT_SORT_BITS_PER_PASS)

// The number of elements dealt with per running thread
#define FFX_PARALLELSORT_ELEMENTS_PER_THREAD	    4

// The number of threads to execute in parallel for each dispatch group
#define FFX_PARALLELSORT_THREADGROUP_SIZE		    128


// -------- Actual code

groupshared uint gs_FFX_PARALLELSORT_Histogram[FFX_PARALLELSORT_THREADGROUP_SIZE * FFX_PARALLELSORT_SORT_BIN_COUNT];
void ffxParallelSortCountUInt(uint localID, uint groupID, uint ShiftBit)
{
    // Start by clearing our local counts in LDS
    for (int i = 0; i < FFX_PARALLELSORT_SORT_BIN_COUNT; i++)
        gs_FFX_PARALLELSORT_Histogram[(i * FFX_PARALLELSORT_THREADGROUP_SIZE) + localID] = 0;

    // Wait for everyone to catch up
    GroupMemoryBarrierWithGroupSync();

    // Data is processed in blocks, and how many we process can changed based on how much data we are processing
    // versus how many thread groups we are processing with
    int BlockSize = FFX_PARALLELSORT_ELEMENTS_PER_THREAD * FFX_PARALLELSORT_THREADGROUP_SIZE;

    // Figure out this thread group's index into the block data (taking into account thread groups that need to do extra reads)
    uint NumBlocksPerThreadGroup = FfxNumBlocksPerThreadGroup();
    uint NumThreadGroups = FfxNumThreadGroups();
    uint NumThreadGroupsWithAdditionalBlocks = FfxNumThreadGroupsWithAdditionalBlocks();
    uint NumKeys = FfxNumKeys();

    uint ThreadgroupBlockStart = (BlockSize * NumBlocksPerThreadGroup * groupID);
    uint NumBlocksToProcess = NumBlocksPerThreadGroup;

    if (groupID >= NumThreadGroups - NumThreadGroupsWithAdditionalBlocks)
    {
        ThreadgroupBlockStart += (groupID - (NumThreadGroups - NumThreadGroupsWithAdditionalBlocks)) * BlockSize;
        NumBlocksToProcess++;
    }

    // Get the block start index for this thread
    uint BlockIndex = ThreadgroupBlockStart + localID;

    // Count value occurrence
    for (uint BlockCount = 0; BlockCount < NumBlocksToProcess; BlockCount++, BlockIndex += BlockSize)
    {
        uint DataIndex = BlockIndex;

        // Pre-load the key values in order to hide some of the read latency
        uint srcKeys[FFX_PARALLELSORT_ELEMENTS_PER_THREAD];
        srcKeys[0] = FfxLoadKey(DataIndex);
        srcKeys[1] = FfxLoadKey(DataIndex + FFX_PARALLELSORT_THREADGROUP_SIZE);
        srcKeys[2] = FfxLoadKey(DataIndex + (FFX_PARALLELSORT_THREADGROUP_SIZE * 2));
        srcKeys[3] = FfxLoadKey(DataIndex + (FFX_PARALLELSORT_THREADGROUP_SIZE * 3));

        for (uint i = 0; i < FFX_PARALLELSORT_ELEMENTS_PER_THREAD; i++)
        {
            if (DataIndex < NumKeys)
            {
                uint localKey = (srcKeys[i] >> ShiftBit) & 0xf;
                InterlockedAdd(gs_FFX_PARALLELSORT_Histogram[(localKey * FFX_PARALLELSORT_THREADGROUP_SIZE) + localID], 1);
                DataIndex += FFX_PARALLELSORT_THREADGROUP_SIZE;
            }
        }
    }

    // Even though our LDS layout guarantees no collisions, our thread group size is greater than a wave
    // so we need to make sure all thread groups are done counting before we start tallying up the results
    GroupMemoryBarrierWithGroupSync();

    if (localID < FFX_PARALLELSORT_SORT_BIN_COUNT)
    {
        uint sum = 0;
        for (int i = 0; i < FFX_PARALLELSORT_THREADGROUP_SIZE; i++)
        {
            sum += gs_FFX_PARALLELSORT_Histogram[localID * FFX_PARALLELSORT_THREADGROUP_SIZE + i];
        }
        FfxStoreSum(localID * NumThreadGroups + groupID, sum);
    }
}

groupshared uint gs_FFX_PARALLELSORT_LDSSums[FFX_PARALLELSORT_THREADGROUP_SIZE];
uint ffxParallelSortThreadgroupReduce(uint localSum, uint localID)
{
#if defined(SHADER_AVAILABLE_WAVEBASIC)
    // Do wave local reduce
    uint waveReduced = WaveActiveSum(localSum);

    // First lane in a wave writes out wave reduction to LDS (this accounts for num waves per group greater than HW wave size)
    // Note that some hardware with very small HW wave sizes (i.e. <= 8) may exhibit issues with this algorithm, and have not been tested.
    uint waveID = localID / WaveGetLaneCount();
    if (WaveIsFirstLane())
        gs_FFX_PARALLELSORT_LDSSums[waveID] = waveReduced;

    // Wait for everyone to catch up
    GroupMemoryBarrierWithGroupSync();

    // First wave worth of threads sum up wave reductions
    if (!waveID)
        waveReduced = WaveActiveSum((localID < FFX_PARALLELSORT_THREADGROUP_SIZE / WaveGetLaneCount()) ? gs_FFX_PARALLELSORT_LDSSums[localID] : 0);

    // Returned the reduced sum
    return waveReduced;
#else
    // No wave ops, do a stupidly slow emulation (should be possible to make it faster!)
    //@TODO: not working correctly yet!
    gs_FFX_PARALLELSORT_LDSSums[localID] = localSum;
    GroupMemoryBarrierWithGroupSync();
    uint reduced = 0;
    for (uint i = 0; i < FFX_PARALLELSORT_THREADGROUP_SIZE; ++i)
        reduced += gs_FFX_PARALLELSORT_LDSSums[i];
    return reduced;
#endif
}

void ffxParallelSortReduceCount(uint localID, uint groupID)
{
    uint NumReduceThreadgroupPerBin = FfxNumReduceThreadgroupPerBin();
    uint NumThreadGroups = FfxNumThreadGroups();

    // Figure out what bin data we are reducing
    uint BinID = groupID / NumReduceThreadgroupPerBin;
    uint BinOffset = BinID * NumThreadGroups;

    // Get the base index for this thread group
    uint BaseIndex = (groupID % NumReduceThreadgroupPerBin) * FFX_PARALLELSORT_ELEMENTS_PER_THREAD * FFX_PARALLELSORT_THREADGROUP_SIZE;

    // Calculate partial sums for entries this thread reads in
    uint threadgroupSum = 0;
    for (uint i = 0; i < FFX_PARALLELSORT_ELEMENTS_PER_THREAD; ++i)
    {
        uint DataIndex = BaseIndex + (i * FFX_PARALLELSORT_THREADGROUP_SIZE) + localID;
        threadgroupSum += (DataIndex < NumThreadGroups) ? FfxLoadSum(BinOffset + DataIndex) : 0;
    }

    // Reduce across the entirety of the thread group
    threadgroupSum = ffxParallelSortThreadgroupReduce(threadgroupSum, localID);

    // First thread of the group writes out the reduced sum for the bin
    if (localID == 0)
        FfxStoreReduce(groupID, threadgroupSum);

    // What this will look like in the reduced table is:
    //	[ [bin0 ... bin0] [bin1 ... bin1] ... ]
}

uint ffxParallelSortBlockScanPrefix(uint localSum, uint localID)
{
#if defined(SHADER_AVAILABLE_WAVEBASIC)
    // Do wave local scan-prefix
    uint wavePrefixed = WavePrefixSum(localSum);

    // Since we are dealing with thread group sizes greater than HW wave size, we need to account for what wave we are in.
    uint waveID = localID / WaveGetLaneCount();
    uint laneID = WaveGetLaneIndex();

    // Last element in a wave writes out partial sum to LDS
    if (laneID == WaveGetLaneCount() - 1)
        gs_FFX_PARALLELSORT_LDSSums[waveID] = wavePrefixed + localSum;

    // Wait for everyone to catch up
    GroupMemoryBarrierWithGroupSync();

    // First wave prefixes partial sums
    if (!waveID)
        gs_FFX_PARALLELSORT_LDSSums[localID] = WavePrefixSum(gs_FFX_PARALLELSORT_LDSSums[localID]);

    // Wait for everyone to catch up
    GroupMemoryBarrierWithGroupSync();

    // Add the partial sums back to each wave prefix
    wavePrefixed += gs_FFX_PARALLELSORT_LDSSums[waveID];

    return wavePrefixed;
#else
    // No wave ops, do a stupidly slow emulation (should be possible to make it faster!)
    //@TODO: not working correctly yet!
    gs_FFX_PARALLELSORT_LDSSums[localID] = localSum;
    GroupMemoryBarrierWithGroupSync();
    uint prefix = 0;
    for (uint i = 0; i < localID; ++i)
    {
        prefix += gs_FFX_PARALLELSORT_LDSSums[i];
    }
    GroupMemoryBarrierWithGroupSync();
    return prefix;
#endif
}

// This is to transform uncoalesced loads into coalesced loads and
// then scattered loads from LDS
groupshared uint gs_FFX_PARALLELSORT_LDS[FFX_PARALLELSORT_ELEMENTS_PER_THREAD][FFX_PARALLELSORT_THREADGROUP_SIZE];
void ffxParallelSortScanPrefix(uint numValuesToScan, uint localID, uint groupID, uint BinOffset, uint BaseIndex, bool AddPartialSums)
{
    uint i;

    // Perform coalesced loads into LDS
    for (i = 0; i < FFX_PARALLELSORT_ELEMENTS_PER_THREAD; i++)
    {
        uint DataIndex = BaseIndex + (i * FFX_PARALLELSORT_THREADGROUP_SIZE) + localID;

        uint col = ((i * FFX_PARALLELSORT_THREADGROUP_SIZE) + localID) / FFX_PARALLELSORT_ELEMENTS_PER_THREAD;
        uint row = ((i * FFX_PARALLELSORT_THREADGROUP_SIZE) + localID) % FFX_PARALLELSORT_ELEMENTS_PER_THREAD;
        gs_FFX_PARALLELSORT_LDS[row][col] = (DataIndex < numValuesToScan) ? FfxLoadScanSource(BinOffset + DataIndex) : 0;
    }

    // Wait for everyone to catch up
    GroupMemoryBarrierWithGroupSync();

    uint threadgroupSum = 0;
    // Calculate the local scan-prefix for current thread
    for (i = 0; i < FFX_PARALLELSORT_ELEMENTS_PER_THREAD; i++)
    {
        uint tmp = gs_FFX_PARALLELSORT_LDS[i][localID];
        gs_FFX_PARALLELSORT_LDS[i][localID] = threadgroupSum;
        threadgroupSum += tmp;
    }

    // Scan prefix partial sums
    threadgroupSum = ffxParallelSortBlockScanPrefix(threadgroupSum, localID);

    // Add reduced partial sums if requested
    uint partialSum = 0;
    if (AddPartialSums)
    {
        // Partial sum additions are a little special as they are tailored to the optimal number of
        // thread groups we ran in the beginning, so need to take that into account
        partialSum = FfxLoadScanScratch(groupID);
    }

    // Add the block scanned-prefixes back in
    for (i = 0; i < FFX_PARALLELSORT_ELEMENTS_PER_THREAD; i++)
        gs_FFX_PARALLELSORT_LDS[i][localID] += threadgroupSum;

    // Wait for everyone to catch up
    GroupMemoryBarrierWithGroupSync();

    // Perform coalesced writes to scan dst
    for (i = 0; i < FFX_PARALLELSORT_ELEMENTS_PER_THREAD; i++)
    {
        uint DataIndex = BaseIndex + (i * FFX_PARALLELSORT_THREADGROUP_SIZE) + localID;

        uint col = ((i * FFX_PARALLELSORT_THREADGROUP_SIZE) + localID) / FFX_PARALLELSORT_ELEMENTS_PER_THREAD;
        uint row = ((i * FFX_PARALLELSORT_THREADGROUP_SIZE) + localID) % FFX_PARALLELSORT_ELEMENTS_PER_THREAD;

        if (DataIndex < numValuesToScan)
            FfxStoreScanDest(BinOffset + DataIndex, gs_FFX_PARALLELSORT_LDS[row][col] + partialSum);
    }
}

// Offset cache to avoid loading the offsets all the time
groupshared uint gs_FFX_PARALLELSORT_BinOffsetCache[FFX_PARALLELSORT_THREADGROUP_SIZE];
// Local histogram for offset calculations
groupshared uint gs_FFX_PARALLELSORT_LocalHistogram[FFX_PARALLELSORT_SORT_BIN_COUNT];
// Scratch area for algorithm
groupshared uint gs_FFX_PARALLELSORT_LDSScratch[FFX_PARALLELSORT_THREADGROUP_SIZE];

void ffxParallelSortScatterUInt(uint localID, uint groupID, uint ShiftBit)
{
    uint NumBlocksPerThreadGroup = FfxNumBlocksPerThreadGroup();
    uint NumThreadGroups = FfxNumThreadGroups();
    uint NumThreadGroupsWithAdditionalBlocks = FfxNumThreadGroupsWithAdditionalBlocks();
    uint NumKeys = FfxNumKeys();

    // Load the sort bin threadgroup offsets into LDS for faster referencing
    if (localID < FFX_PARALLELSORT_SORT_BIN_COUNT)
        gs_FFX_PARALLELSORT_BinOffsetCache[localID] = FfxLoadSum(localID * NumThreadGroups + groupID);

    // Wait for everyone to catch up
    GroupMemoryBarrierWithGroupSync();

    // Data is processed in blocks, and how many we process can changed based on how much data we are processing
    // versus how many thread groups we are processing with
    int BlockSize = FFX_PARALLELSORT_ELEMENTS_PER_THREAD * FFX_PARALLELSORT_THREADGROUP_SIZE;

    // Figure out this thread group's index into the block data (taking into account thread groups that need to do extra reads)
    uint ThreadgroupBlockStart = (BlockSize * NumBlocksPerThreadGroup * groupID);
    uint NumBlocksToProcess = NumBlocksPerThreadGroup;

    if (groupID >= NumThreadGroups - NumThreadGroupsWithAdditionalBlocks)
    {
        ThreadgroupBlockStart += (groupID - (NumThreadGroups - NumThreadGroupsWithAdditionalBlocks)) * BlockSize;
        NumBlocksToProcess++;
    }

    // Get the block start index for this thread
    uint BlockIndex = ThreadgroupBlockStart + localID;

    // Count value occurences
    uint newCount;
    for (uint BlockCount = 0; BlockCount < NumBlocksToProcess; BlockCount++, BlockIndex += BlockSize)
    {
        uint DataIndex = BlockIndex;

        // Pre-load the key values in order to hide some of the read latency
        uint srcKeys[FFX_PARALLELSORT_ELEMENTS_PER_THREAD];
        srcKeys[0] = FfxLoadKey(DataIndex);
        srcKeys[1] = FfxLoadKey(DataIndex + FFX_PARALLELSORT_THREADGROUP_SIZE);
        srcKeys[2] = FfxLoadKey(DataIndex + (FFX_PARALLELSORT_THREADGROUP_SIZE * 2));
        srcKeys[3] = FfxLoadKey(DataIndex + (FFX_PARALLELSORT_THREADGROUP_SIZE * 3));

        uint srcValues[FFX_PARALLELSORT_ELEMENTS_PER_THREAD];
        srcValues[0] = FfxLoadPayload(DataIndex);
        srcValues[1] = FfxLoadPayload(DataIndex + FFX_PARALLELSORT_THREADGROUP_SIZE);
        srcValues[2] = FfxLoadPayload(DataIndex + (FFX_PARALLELSORT_THREADGROUP_SIZE * 2));
        srcValues[3] = FfxLoadPayload(DataIndex + (FFX_PARALLELSORT_THREADGROUP_SIZE * 3));

        for (int i = 0; i < FFX_PARALLELSORT_ELEMENTS_PER_THREAD; i++)
        {
            // Clear the local histogram
            if (localID < FFX_PARALLELSORT_SORT_BIN_COUNT)
                gs_FFX_PARALLELSORT_LocalHistogram[localID] = 0;

            uint localKey = (DataIndex < NumKeys ? srcKeys[i] : 0xffffffff);
            uint localValue = (DataIndex < NumKeys ? srcValues[i] : 0);

            // Sort the keys locally in LDS
            for (uint bitShift = 0; bitShift < FFX_PARALLELSORT_SORT_BITS_PER_PASS; bitShift += 2)
            {
                // Figure out the keyIndex
                uint keyIndex = (localKey >> ShiftBit) & 0xf;
                uint bitKey = (keyIndex >> bitShift) & 0x3;

                // Create a packed histogram
                uint packedHistogram = 1U << (bitKey * 8);

                // Sum up all the packed keys (generates counted offsets up to current thread group)
                uint localSum = ffxParallelSortBlockScanPrefix(packedHistogram, localID);

                // Last thread stores the updated histogram counts for the thread group
                // Scratch = 0xsum3|sum2|sum1|sum0 for thread group
                if (localID == (FFX_PARALLELSORT_THREADGROUP_SIZE - 1))
                    gs_FFX_PARALLELSORT_LDSScratch[0] = localSum + packedHistogram;

                // Wait for everyone to catch up
                GroupMemoryBarrierWithGroupSync();

                // Load the sums value for the thread group
                packedHistogram = gs_FFX_PARALLELSORT_LDSScratch[0];

                // Add prefix offsets for all 4 bit "keys" (packedHistogram = 0xsum2_1_0|sum1_0|sum0|0)
                packedHistogram = (packedHistogram << 8) + (packedHistogram << 16) + (packedHistogram << 24);

                // Calculate the proper offset for this thread's value
                localSum += packedHistogram;

                // Calculate target offset
                uint keyOffset = (localSum >> (bitKey * 8)) & 0xff;

                // Re-arrange the keys (store, sync, load)
                gs_FFX_PARALLELSORT_LDSSums[keyOffset] = localKey;
                GroupMemoryBarrierWithGroupSync();
                localKey = gs_FFX_PARALLELSORT_LDSSums[localID];

                // Wait for everyone to catch up
                GroupMemoryBarrierWithGroupSync();

                // Re-arrange the values if we have them (store, sync, load)
                gs_FFX_PARALLELSORT_LDSSums[keyOffset] = localValue;
                GroupMemoryBarrierWithGroupSync();
                localValue = gs_FFX_PARALLELSORT_LDSSums[localID];

                // Wait for everyone to catch up
                GroupMemoryBarrierWithGroupSync();
            }

            // Need to recalculate the keyIndex on this thread now that values have been copied around the thread group
            uint keyIndex = (localKey >> ShiftBit) & 0xf;

            // Reconstruct histogram
            InterlockedAdd(gs_FFX_PARALLELSORT_LocalHistogram[keyIndex], 1);

            // Wait for everyone to catch up
            GroupMemoryBarrierWithGroupSync();

            // Prefix histogram
            uint histogramLocalSum = localID < FFX_PARALLELSORT_SORT_BIN_COUNT ? gs_FFX_PARALLELSORT_LocalHistogram[localID] : 0;
            #if defined(SHADER_AVAILABLE_WAVEBASIC)
            uint histogramPrefixSum = WavePrefixSum(histogramLocalSum);
            #else
            // No wave ops, do a stupidly slow emulation (should be possible to make it faster!)
            //@TODO: not working correctly yet!
            gs_FFX_PARALLELSORT_LDSSums[localID] = histogramLocalSum;
            GroupMemoryBarrierWithGroupSync();
            uint histogramPrefixSum = 0;
            for (uint hi = 0; hi < localID; ++hi)
                histogramPrefixSum += gs_FFX_PARALLELSORT_LDSSums[hi];
            GroupMemoryBarrierWithGroupSync();
            #endif

            // Broadcast prefix-sum via LDS
            if (localID < FFX_PARALLELSORT_SORT_BIN_COUNT)
                gs_FFX_PARALLELSORT_LDSScratch[localID] = histogramPrefixSum;

            // Get the global offset for this key out of the cache
            uint globalOffset = gs_FFX_PARALLELSORT_BinOffsetCache[keyIndex];

            // Wait for everyone to catch up
            GroupMemoryBarrierWithGroupSync();

            // Get the local offset (at this point the keys are all in increasing order from 0 -> num bins in localID 0 -> thread group size)
            uint localOffset = localID - gs_FFX_PARALLELSORT_LDSScratch[keyIndex];

            // Write to destination
            uint totalOffset = globalOffset + localOffset;

            if (totalOffset < NumKeys)
            {
                FfxStoreKey(totalOffset, localKey);
                FfxStorePayload(totalOffset, localValue);
            }

            // Wait for everyone to catch up
            GroupMemoryBarrierWithGroupSync();

            // Update the cached histogram for the next set of entries
            if (localID < FFX_PARALLELSORT_SORT_BIN_COUNT)
                gs_FFX_PARALLELSORT_BinOffsetCache[localID] += gs_FFX_PARALLELSORT_LocalHistogram[localID];

            DataIndex += FFX_PARALLELSORT_THREADGROUP_SIZE;	// Increase the data offset by thread group size
        }
    }
}

// -------- Kernel entry points

// Buffers: rw_sum_table, rw_reduce_table
[numthreads(FFX_PARALLELSORT_THREADGROUP_SIZE, 1, 1)]
void FfxParallelSortReduce(uint LocalID : SV_GroupThreadID, uint GroupID : SV_GroupID)
{
    ffxParallelSortReduceCount(LocalID, GroupID);
}

// Buffers: rw_scan_source, rw_scan_dest, rw_scan_scratch
[numthreads(FFX_PARALLELSORT_THREADGROUP_SIZE, 1, 1)]
void FfxParallelSortScanAdd(uint LocalID : SV_GroupThreadID, uint GroupID : SV_GroupID)
{
    // When doing adds, we need to access data differently because reduce
    // has a more specialized access pattern to match optimized count
    // Access needs to be done similarly to reduce
    // Figure out what bin data we are reducing
    uint BinID = GroupID / FfxNumReduceThreadgroupPerBin();
    uint BinOffset = BinID * FfxNumThreadGroups();

    // Get the base index for this thread group
    uint BaseIndex = (GroupID % FfxNumReduceThreadgroupPerBin()) * FFX_PARALLELSORT_ELEMENTS_PER_THREAD * FFX_PARALLELSORT_THREADGROUP_SIZE;

    ffxParallelSortScanPrefix(FfxNumThreadGroups(), LocalID, GroupID, BinOffset, BaseIndex, true);
}

// Buffers: rw_scan_source, rw_scan_dest
[numthreads(FFX_PARALLELSORT_THREADGROUP_SIZE, 1, 1)]
void FfxParallelSortScan(uint LocalID : SV_GroupThreadID, uint GroupID : SV_GroupID)
{
    uint BaseIndex = FFX_PARALLELSORT_ELEMENTS_PER_THREAD * FFX_PARALLELSORT_THREADGROUP_SIZE * GroupID;
    ffxParallelSortScanPrefix(FfxNumScanValues(), LocalID, GroupID, 0, BaseIndex, false);
}

// Buffers: rw_source_keys, rw_dest_keys, rw_sum_table, rw_source_payloads, rw_dest_payloads
[numthreads(FFX_PARALLELSORT_THREADGROUP_SIZE, 1, 1)]
void FfxParallelSortScatter(uint LocalID : SV_GroupThreadID, uint GroupID : SV_GroupID)
{
    ffxParallelSortScatterUInt(LocalID, GroupID, FfxShiftBit());
}

// Buffers: rw_source_keys, rw_sum_table
[numthreads(FFX_PARALLELSORT_THREADGROUP_SIZE, 1, 1)]
void FfxParallelSortCount(uint LocalID : SV_GroupThreadID, uint GroupID : SV_GroupID)
{
    ffxParallelSortCountUInt(LocalID, GroupID, FfxShiftBit());
}
