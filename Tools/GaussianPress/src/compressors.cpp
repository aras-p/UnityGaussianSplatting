#include "compressors.h"
#include <stdio.h>
#include <assert.h>

#include <string>

#include "simd.h"


static std::vector<int> GetGenericLevelRange(CompressionFormat format)
{
    switch (format)
    {
    case kCompressionZstd:
        return { -5, -1, 1, 5, 9 };
    case kCompressionLZ4:
        return { -5, 0, 1, 9 };
    default:
        return { 0 };
    }
}

uint8_t* GenericCompressor::Compress(int level, const void* data, size_t itemCount, size_t itemStride, size_t& outSize)
{
    size_t dataSize = itemCount * itemStride;
    size_t bound = compress_calc_bound(dataSize, m_Format);
    uint8_t* cmp = new uint8_t[bound];
    outSize = compress_data(data, dataSize, cmp, bound, m_Format, level);
    return cmp;
}

void GenericCompressor::Decompress(const uint8_t* cmp, size_t cmpSize, void* data, size_t itemCount, size_t itemStride)
{
    size_t dataSize = itemCount * itemStride;
    decompress_data(cmp, cmpSize, data, dataSize, m_Format);
}

static const char* kCompressionFormatNames[] = {
    "zstd",
    "lz4",
};
static_assert(sizeof(kCompressionFormatNames) / sizeof(kCompressionFormatNames[0]) == kCompressionCount);

void GenericCompressor::PrintName(size_t bufSize, char* buf) const
{
    snprintf(buf, bufSize, "%s", kCompressionFormatNames[m_Format]);
}

std::vector<int> GenericCompressor::GetLevels() const
{
    return GetGenericLevelRange(m_Format);
}

static uint8_t* CompressGeneric(CompressionFormat format, int level, uint8_t* data, size_t dataSize, size_t& outSize)
{
    if (format == kCompressionCount)
    {
        outSize = dataSize;
        return data;
    }
    size_t bound = compress_calc_bound(dataSize, format);
    uint8_t* cmp = new uint8_t[bound + 4];
    *(uint32_t*)cmp = uint32_t(dataSize); // store orig size at start
    outSize = compress_data(data, dataSize, cmp + 4, bound, format, level) + 4;
    delete[] data;
    return cmp;
}

static uint8_t* DecompressGeneric(CompressionFormat format, const uint8_t* cmp, size_t cmpSize, size_t& outSize)
{
    if (format == kCompressionCount)
    {
        outSize = cmpSize;
        return (uint8_t*)cmp;
    }
    uint32_t decSize = *(uint32_t*)cmp; // fetch orig size from start
    uint8_t* decomp = new uint8_t[decSize];
    outSize = decompress_data(cmp + 4, cmpSize - 4, decomp, decSize, format);
    return decomp;
}

uint8_t* MeshOptCompressor::Compress(int level, const void* data, size_t itemCount, size_t itemStride, size_t& outSize)
{
    size_t dataSize = itemCount * itemStride;
    size_t moBound = compress_meshopt_vertex_attribute_bound(itemCount, itemStride);
    uint8_t* moCmp = new uint8_t[moBound];
    size_t moSize = compress_meshopt_vertex_attribute(data, itemCount, itemStride, moCmp, moBound);
    return CompressGeneric(m_Format, level, moCmp, moSize, outSize);
}

void MeshOptCompressor::Decompress(const uint8_t* cmp, size_t cmpSize, void* data, size_t itemCount, size_t itemStride)
{
    size_t dataSize = itemCount * itemStride;

    size_t decompSize;
    uint8_t* decomp = DecompressGeneric(m_Format, cmp, cmpSize, decompSize);

    decompress_meshopt_vertex_attribute(decomp, decompSize, itemCount, itemStride, data);
    if (decomp != cmp) delete[] decomp;
}

std::vector<int> MeshOptCompressor::GetLevels() const
{
    return GetGenericLevelRange(m_Format);
}

void MeshOptCompressor::PrintName(size_t bufSize, char* buf) const
{
    if (m_Format == kCompressionCount)
        snprintf(buf, bufSize, "meshopt");
    else
        snprintf(buf, bufSize, "meshopt-%s", kCompressionFormatNames[m_Format]);
}
