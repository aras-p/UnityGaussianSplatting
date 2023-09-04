#include "filters.h"
#include "simd.h"
#include <assert.h>
#include <string.h>

const size_t kMaxChannels = 256;
static_assert(kMaxChannels >= 16, "max channels can't be lower than simd width");


// Transpose NxM byte matrix, with faster code paths for rows=16, cols=multiple-of-16 case.
// Largely based on https://fgiesen.wordpress.com/2013/07/09/simd-transposes-1/ and
// https://fgiesen.wordpress.com/2013/08/29/simd-transposes-2/
static void EvenOddInterleave16(const Bytes16* a, Bytes16* b, int astride = 1)
{
    int bidx = 0;
    for (int i = 0; i < 8; ++i)
    {
        b[bidx] = SimdInterleaveL(a[i * astride], a[(i + 8) * astride]); bidx++;
        b[bidx] = SimdInterleaveR(a[i * astride], a[(i + 8) * astride]); bidx++;
    }
}
static void Transpose16x16(const Bytes16* a, Bytes16* b, int astride = 1)
{
    Bytes16 tmp1[16], tmp2[16];
    EvenOddInterleave16(a, tmp1, astride);
    EvenOddInterleave16(tmp1, tmp2);
    EvenOddInterleave16(tmp2, tmp1);
    EvenOddInterleave16(tmp1, b);
}
static void Transpose(const uint8_t* a, uint8_t* b, int cols, int rows)
{
    if (rows == 16 && ((cols % 16) == 0))
    {
        int blocks = cols / rows;
        for (int i = 0; i < blocks; ++i)
        {
            Transpose16x16(((const Bytes16*)a) + i, ((Bytes16*)b) + i * 16, blocks);
        }
    }
    else
    {
        for (int j = 0; j < rows; ++j)
        {
            for (int i = 0; i < cols; ++i)
            {
                b[i * rows + j] = a[j * cols + i];
            }
        }
    }
}

// Fetch 16 N-sized items, transpose, SIMD delta, write N separate 16-sized items
void Filter_ByteDelta(const uint8_t* src, uint8_t* dst, size_t channels, size_t dataElems)
{
    uint8_t* dstPtr = dst;
    int64_t ip = 0;
    
    const uint8_t* srcPtr = src;
    // simd loop
    Bytes16 prev[kMaxChannels] = {};
    for (; ip < int64_t(dataElems) - 15; ip += 16)
    {
        // fetch 16 data items
        uint8_t curr[kMaxChannels * 16];
        memcpy(curr, srcPtr, channels * 16);
        srcPtr += channels * 16;
        // transpose so we have 16 bytes for each channel
        Bytes16 currT[kMaxChannels];
        Transpose(curr, (uint8_t*)currT, channels, 16);
        // delta within each channel, store
        for (int ich = 0; ich < channels; ++ich)
        {
            Bytes16 v = currT[ich];
            Bytes16 delta = SimdSub(v, SimdConcat<15>(v, prev[ich]));
            SimdStore(dstPtr + dataElems * ich, delta);
            prev[ich] = v;
        }
        dstPtr += 16;
    }
    // any remaining leftover
    if (ip < int64_t(dataElems))
    {
        uint8_t prev1[kMaxChannels];
        for (int ich = 0; ich < channels; ++ich)
            prev1[ich] = SimdGetLane<15>(prev[ich]);
        for (; ip < int64_t(dataElems); ip++)
        {
            for (int ich = 0; ich < channels; ++ich)
            {
                uint8_t v = *srcPtr;
                srcPtr++;
                dstPtr[dataElems * ich] = v - prev1[ich];
                prev1[ich] = v;
            }
            dstPtr++;
        }
    }
}

// Fetch 16b from N streams, prefix sum SIMD undelta, transpose, sequential write 16xN chunk.
void UnFilter_ByteDelta(const uint8_t* src, uint8_t* dst, size_t channels, size_t dataElems)
{
    uint8_t* dstPtr = dst;
    int64_t ip = 0;

    // simd loop: fetch 16 bytes from each stream
    Bytes16 curr[kMaxChannels] = {};
    const Bytes16 hibyte = SimdSet1(15);
    for (; ip < int64_t(dataElems) - 15; ip += 16)
    {
        // fetch 16 bytes from each channel, prefix-sum un-delta
        const uint8_t* srcPtr = src + ip;
        for (int ich = 0; ich < channels; ++ich)
        {
            Bytes16 v = SimdLoad(srcPtr);
            // un-delta via prefix sum
            curr[ich] = SimdAdd(SimdPrefixSum(v), SimdShuffle(curr[ich], hibyte));
            srcPtr += dataElems;
        }

        // now transpose 16xChannels matrix
        uint8_t currT[kMaxChannels * 16];
        Transpose((const uint8_t*)curr, currT, 16, channels);

        // and store into destination
        memcpy(dstPtr, currT, 16 * channels);
        dstPtr += 16 * channels;
    }

    // any remaining leftover
    if (ip < int64_t(dataElems))
    {
        uint8_t curr1[kMaxChannels];
        for (int ich = 0; ich < channels; ++ich)
            curr1[ich] = SimdGetLane<15>(curr[ich]);
        for (; ip < int64_t(dataElems); ip++)
        {
            const uint8_t* srcPtr = src + ip;
            for (int ich = 0; ich < channels; ++ich)
            {
                uint8_t v = *srcPtr + curr1[ich];
                curr1[ich] = v;
                *dstPtr = v;
                srcPtr += dataElems;
                dstPtr += 1;
            }
        }
    }
}
