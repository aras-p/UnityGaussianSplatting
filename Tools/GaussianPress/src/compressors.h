#pragma once
#include "compression_helpers.h"
#include <stddef.h>
#include <vector>

struct Compressor
{
	virtual ~Compressor() {}
	virtual uint8_t* Compress(int level, const void* data, size_t itemCount, size_t itemStride, size_t& outSize) = 0;
	virtual void Decompress(const uint8_t* cmp, size_t cmpSize, void* data, size_t itemCount, size_t itemStride) = 0;
	virtual std::vector<int> GetLevels() const { return {0}; }
	virtual void PrintName(size_t bufSize, char* buf) const = 0;
};

struct GenericCompressor : public Compressor
{
	GenericCompressor(CompressionFormat format) : m_Format(format) {}
	virtual uint8_t* Compress(int level, const void* data, size_t itemCount, size_t itemStride, size_t& outSize);
	virtual void Decompress(const uint8_t* cmp, size_t cmpSize, void* data, size_t itemCount, size_t itemStride);
	virtual std::vector<int> GetLevels() const;
	virtual void PrintName(size_t bufSize, char* buf) const;
	CompressionFormat m_Format;
};

struct MeshOptCompressor : public Compressor
{
	MeshOptCompressor(CompressionFormat format) : m_Format(format) {}
	virtual uint8_t* Compress(int level, const void* data, size_t itemCount, size_t itemStride, size_t& outSize);
	virtual void Decompress(const uint8_t* cmp, size_t cmpSize, void* data, size_t itemCount, size_t itemStride);
	virtual std::vector<int> GetLevels() const;
	virtual void PrintName(size_t bufSize, char* buf) const;
	CompressionFormat m_Format;
};
