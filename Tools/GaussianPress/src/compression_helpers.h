#pragma once

#include <stdint.h>
#include <stddef.h>

// mesh optimizer
size_t compress_meshopt_vertex_attribute_bound(size_t vertexCount, size_t vertexSize);
size_t compress_meshopt_vertex_attribute(const void* src, size_t vertexCount, size_t vertexSize, void* dst, size_t dstSize);
size_t decompress_meshopt_vertex_attribute(const void* src, size_t srcSize, size_t vertexCount, size_t vertexSize, void* dst);

// generic lossless compressors
enum CompressionFormat
{
	kCompressionZstd = 0,
	kCompressionLZ4,
	kCompressionCount
};
size_t compress_calc_bound(size_t srcSize, CompressionFormat format);
size_t compress_data(const void* src, size_t srcSize, void* dst, size_t dstSize, CompressionFormat format, int level);
size_t decompress_data(const void* src, size_t srcSize, void* dst, size_t dstSize, CompressionFormat format);
