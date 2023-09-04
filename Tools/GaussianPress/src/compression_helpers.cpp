#include "compression_helpers.h"

#include <meshoptimizer.h>
#include <string.h>
#include <zstd.h>
#include <lz4.h>
#include <lz4hc.h>
#include <stdio.h>

size_t compress_meshopt_vertex_attribute_bound(size_t vertexCount, size_t vertexSize)
{
	return meshopt_encodeVertexBufferBound(vertexCount, vertexSize);
}
size_t compress_meshopt_vertex_attribute(const void* src, size_t vertexCount, size_t vertexSize, void* dst, size_t dstSize)
{
	return meshopt_encodeVertexBuffer((unsigned char*)dst, dstSize, src, vertexCount, vertexSize);
}
size_t decompress_meshopt_vertex_attribute(const void* src, size_t srcSize, size_t vertexCount, size_t vertexSize, void* dst)
{
	return meshopt_decodeVertexBuffer(dst, vertexCount, vertexSize, (const unsigned char*)src, srcSize);
}


size_t compress_calc_bound(size_t srcSize, CompressionFormat format)
{
	if (srcSize == 0)
		return 0;
	switch (format)
	{
	case kCompressionZstd: return ZSTD_compressBound(srcSize);
	case kCompressionLZ4: return LZ4_compressBound(int(srcSize));
	default: return 0;
	}	
}
size_t compress_data(const void* src, size_t srcSize, void* dst, size_t dstSize, CompressionFormat format, int level)
{
	if (srcSize == 0)
		return 0;
	switch (format)
	{
	case kCompressionZstd: return ZSTD_compress(dst, dstSize, src, srcSize, level);
	case kCompressionLZ4:
		if (level > 0)
			return LZ4_compress_HC((const char*)src, (char*)dst, (int)srcSize, (int)dstSize, level);
		return LZ4_compress_fast((const char*)src, (char*)dst, (int)srcSize, (int)dstSize, (level > 0 ? level : -level) * 10);
	default: return 0;
	}
}
size_t decompress_data(const void* src, size_t srcSize, void* dst, size_t dstSize, CompressionFormat format)
{
	if (srcSize == 0)
		return 0;
	switch (format)
	{
	case kCompressionZstd: return ZSTD_decompress(dst, dstSize, src, srcSize);
	case kCompressionLZ4: return LZ4_decompress_safe((const char*)src, (char*)dst, (int)srcSize, (int)dstSize);
	default: return 0;
	}	
}
