#include <stdio.h>
#include <vector>
#include "compressors.h"
#include "compression_helpers.h"
#include "filters.h"
#include "systeminfo.h"
#include <math.h>
#include <memory>

#define SOKOL_TIME_IMPL
#include "../libs/sokol_time.h"

constexpr int kRuns = 1;

struct FilterDesc
{
	const char* name = nullptr;
	void (*filterFunc)(const uint8_t* src, uint8_t* dst, size_t channels, size_t dataElems) = nullptr;
	void (*unfilterFunc)(const uint8_t* src, uint8_t* dst, size_t channels, size_t dataElems) = nullptr;
};

static FilterDesc g_FilterByteDelta = { "-bd", Filter_ByteDelta, UnFilter_ByteDelta };

static std::unique_ptr<GenericCompressor> g_CompZstd = std::make_unique<GenericCompressor>(kCompressionZstd);
static std::unique_ptr<GenericCompressor> g_CompLZ4 = std::make_unique<GenericCompressor>(kCompressionLZ4);
static std::unique_ptr<Compressor> g_CompMeshOptZstd = std::make_unique<MeshOptCompressor>(kCompressionZstd);


struct TestFile
{
	const char* title = nullptr;
	const char* path = nullptr;
	std::vector<float> fileData;
	size_t vertexCount = 0;
	size_t vertexStride = 0;
};

enum BlockSize
{
	kBSizeNone,
	kBSize64k,
	kBSize256k,
	kBSize1M,
	kBSize4M,
	kBSize16M,
	kBSize64M,
	kBSizeCount
};
static const size_t kBlockSizeToActualSize[] =
{
	0,
	64 * 1024,
	256 * 1024,
	1024 * 1024,
	4 * 1024 * 1024,
	16 * 1024 * 1024,
	64 * 1024 * 1024,
};
static_assert(sizeof(kBlockSizeToActualSize)/sizeof(kBlockSizeToActualSize[0]) == kBSizeCount, "block size table size mismatch");
static const char* kBlockSizeName[] =
{
	"",
	"-64k",
	"-256k",
	"-1M",
	"-4M",
	"-16M",
	"-64M",
};
static_assert(sizeof(kBlockSizeName) / sizeof(kBlockSizeName[0]) == kBSizeCount, "block size name table size mismatch");

struct CompressorConfig
{
	Compressor* cmp;
	FilterDesc* filter;
	BlockSize blockSizeEnum = kBSizeNone;

	std::string GetName() const
	{
		char buf[100];
		cmp->PrintName(sizeof(buf), buf);
		std::string res = buf;
		if (filter != nullptr)
			res += filter->name;
		res += kBlockSizeName[blockSizeEnum];
		return res;
	}

	uint8_t* CompressWhole(const TestFile& tf, int level, size_t& outCompressedSize)
	{
		const float* srcData = tf.fileData.data();
		uint8_t* filterBuffer = nullptr;
		if (filter)
		{
			filterBuffer = new uint8_t[4 * tf.fileData.size()];
			filter->filterFunc((const uint8_t*)srcData, filterBuffer, tf.vertexStride, tf.vertexCount);
			srcData = (const float*)filterBuffer;
		}

		outCompressedSize = 0;
		uint8_t* compressed = cmp->Compress(level, srcData, tf.vertexCount, tf.vertexStride, outCompressedSize);
		delete[] filterBuffer;
		return compressed;
	}

	uint8_t* Compress(const TestFile& tf, int level, size_t& outCompressedSize)
	{
		if (blockSizeEnum == kBSizeNone)
			return CompressWhole(tf, level, outCompressedSize);

		size_t blockSize = kBlockSizeToActualSize[blockSizeEnum];
		// make sure multiple of data elem size
		blockSize = (blockSize / tf.vertexStride) * tf.vertexStride;

		uint8_t* filterBuffer = nullptr;
		if (filter)
			filterBuffer = new uint8_t[blockSize];

		const size_t dataSize = 4 * tf.fileData.size();
		const uint8_t* srcData = (const uint8_t*)tf.fileData.data();
		uint8_t* compressed = new uint8_t[dataSize + 4];
		size_t srcOffset = 0;
		size_t cmpOffset = 0;
		while (srcOffset < dataSize)
		{
			size_t thisBlockSize = std::min(blockSize, dataSize - srcOffset);
			if (filter)
			{
				filter->filterFunc(srcData + srcOffset, filterBuffer, tf.vertexStride, thisBlockSize / tf.vertexStride);
			}
			size_t thisCmpSize = 0;
			uint8_t* thisCmp = cmp->Compress(level,
				(const float*)(filter ? filterBuffer : srcData + srcOffset),
				thisBlockSize / tf.vertexStride,
				tf.vertexStride,
				thisCmpSize);
			if (cmpOffset + thisCmpSize > dataSize)
			{
				// data is not compressible; fallback to just zero indicator + memcpy
				*(uint32_t*)compressed = 0;
				memcpy(compressed + 4, srcData, dataSize);
				outCompressedSize = dataSize + 4;
				delete[] filterBuffer;
				delete[] thisCmp;
				return compressed;
			}
			// store this chunk size and data
			*(uint32_t*)(compressed + cmpOffset) = uint32_t(thisCmpSize);
			memcpy(compressed + cmpOffset + 4, thisCmp, thisCmpSize);
			delete[] thisCmp;

			srcOffset += blockSize;
			cmpOffset += 4 + thisCmpSize;
		}
		delete[] filterBuffer;
		outCompressedSize = cmpOffset;
		return compressed;
	}

	void DecompressWhole(const TestFile& tf, const uint8_t* compressed, size_t compressedSize, float* dst)
	{
		uint8_t* filterBuffer = nullptr;
		if (filter)
			filterBuffer = new uint8_t[4 * tf.fileData.size()];
		cmp->Decompress(compressed, compressedSize, filter == nullptr ? dst : (float*)filterBuffer, tf.vertexCount, tf.vertexStride);

		if (filter)
		{
			filter->unfilterFunc(filterBuffer, (uint8_t*)dst, tf.vertexStride, tf.vertexCount);
			delete[] filterBuffer;
		}
	}

	void Decompress(const TestFile& tf, const uint8_t* compressed, size_t compressedSize, float* dst)
	{
		if (blockSizeEnum == kBSizeNone)
		{
			DecompressWhole(tf, compressed, compressedSize, dst);
			return;
		}

		uint32_t firstBlockCmpSize = *(const uint32_t*)compressed;
		if (firstBlockCmpSize == 0)
		{
			// it was uncompressible data fallback
			memcpy(dst, compressed + 4, tf.vertexCount * tf.vertexStride);
			return;
		}

		size_t blockSize = kBlockSizeToActualSize[blockSizeEnum];
		// make sure multiple of data elem size
		blockSize = (blockSize / tf.vertexStride) * tf.vertexStride;

		uint8_t* filterBuffer = nullptr;
		if (filter)
			filterBuffer = new uint8_t[blockSize];

		uint8_t* dstData = (uint8_t*)dst;
		const size_t dataSize = 4 * tf.fileData.size();
		
		size_t cmpOffset = 0;
		size_t dstOffset = 0;
		while (cmpOffset < compressedSize)
		{
			size_t thisBlockSize = std::min(blockSize, dataSize - dstOffset);

			uint32_t thisCmpSize = *(const uint32_t*)(compressed + cmpOffset);
			cmp->Decompress(compressed + cmpOffset + 4, thisCmpSize, (float*)(filter == nullptr ? dstData + dstOffset : filterBuffer), thisBlockSize / tf.vertexStride, tf.vertexStride);

			if (filter)
				filter->unfilterFunc(filterBuffer, dstData + dstOffset, tf.vertexStride, thisBlockSize / tf.vertexStride);

			cmpOffset += 4 + thisCmpSize;
			dstOffset += thisBlockSize;
		}
		delete[] filterBuffer;
	}
};

static std::vector<CompressorConfig> g_Compressors;

static void TestCompressors(size_t testFileCount, TestFile* testFiles)
{
	g_Compressors.push_back({ g_CompZstd.get(), &g_FilterByteDelta, kBSize1M });
	g_Compressors.push_back({ g_CompLZ4.get(), &g_FilterByteDelta, kBSize1M });

	g_Compressors.push_back({ g_CompZstd.get(), &g_FilterByteDelta });
	g_Compressors.push_back({ g_CompLZ4.get(), &g_FilterByteDelta });

	g_Compressors.push_back({ g_CompZstd.get() });
	g_Compressors.push_back({ g_CompLZ4.get() });


	size_t maxFloats = 0, totalFloats = 0;
	for (int tfi = 0; tfi < testFileCount; ++tfi)
	{
		size_t floats = testFiles[tfi].fileData.size();
		maxFloats = std::max(maxFloats, floats);
		totalFloats += floats;
	}

	std::vector<float> decompressed(maxFloats);

	struct Result
	{
		int level = 0;
		size_t size = 0;
		double cmpTime = 0;
		double decTime = 0;
	};
	typedef std::vector<Result> LevelResults;
	std::vector<LevelResults> results;
	for (auto& cmp : g_Compressors)
	{
		auto levels = cmp.cmp->GetLevels();
		LevelResults res(levels.size());
		for (size_t i = 0; i < levels.size(); ++i)
			res[i].level = levels[i];
		results.emplace_back(res);
	}

	std::string cmpName;
	for (int ir = 0; ir < kRuns; ++ir)
	{
		printf("Run %i/%i, %zi compressors on %zi files:\n", ir+1, kRuns, g_Compressors.size(), testFileCount);
		for (size_t ic = 0; ic < g_Compressors.size(); ++ic)
		{
			auto& config = g_Compressors[ic];
			cmpName = config.GetName();
			LevelResults& levelRes = results[ic];
			printf("%s: %zi levels:\n", cmpName.c_str(), levelRes.size());
			for (Result& res : levelRes)
			{
				printf(".");
				for (int tfi = 0; tfi < testFileCount; ++tfi)
				{
					const TestFile& tf = testFiles[tfi];

					const float* srcData = tf.fileData.data();
					SysInfoFlushCaches();

					// compress
					uint64_t t0 = stm_now();
					size_t compressedSize = 0;
					uint8_t* compressed = config.Compress(tf, res.level, compressedSize);
					double tComp = stm_sec(stm_since(t0));

					// decompress
					memset(decompressed.data(), 0, 4 * tf.fileData.size());
					SysInfoFlushCaches();
					t0 = stm_now();
					config.Decompress(tf, compressed, compressedSize, decompressed.data());
					double tDecomp = stm_sec(stm_since(t0));

					// stats
					res.size += compressedSize;
					res.cmpTime += tComp;
					res.decTime += tDecomp;

					// check validity
					if (memcmp(tf.fileData.data(), decompressed.data(), 4 * tf.fileData.size()) != 0)
					{
						printf("  ERROR, %s level %i did not decompress back to input on %s\n", cmpName.c_str(), res.level, tf.path);
						for (size_t i = 0; i < 4 * tf.fileData.size(); ++i)
						{
							float va = tf.fileData[i];
							float vb = decompressed[i];
							uint32_t ia = ((const uint32_t*)tf.fileData.data())[i];
							uint32_t ib = ((const uint32_t*)decompressed.data())[i];
							if (va != vb)
							{
								printf("    diff at #%zi: exp %f got %f (%08x %08x)\n", i, va, vb, ia, ib);
								break;
							}
						}
						exit(1);
					}
					delete[] compressed;
				}
			}
			printf("\n");
		}
		printf("\n");
	}

	// normalize results
	int counterRan = 0;
	for (size_t ic = 0; ic < g_Compressors.size(); ++ic)
	{
		Compressor* cmp = g_Compressors[ic].cmp;
		LevelResults& levelRes = results[ic];
		for (Result& res : levelRes)
		{
			res.size /= kRuns;
			res.cmpTime /= kRuns;
			res.decTime /= kRuns;
			++counterRan;
		}
	}
	printf("  Ran %i cases\n", counterRan);


	double oneMB = 1024.0 * 1024.0;
	double oneGB = oneMB * 1024.0;
	double rawSize = (double)(totalFloats * 4);
	// print results to screen
	printf("Compressor     SizeGB CTimeS  DTimeS   Ratio   CGB/s   DGB/s\n");
	printf("%12s %7.3f\n", "Raw", rawSize / oneGB);
	for (size_t ic = 0; ic < g_Compressors.size(); ++ic)
	{
		cmpName = g_Compressors[ic].GetName();
		const LevelResults& levelRes = results[ic];
		for (const Result& res : levelRes)
		{
			char nameBuf[1000];
			snprintf(nameBuf, sizeof(nameBuf), "%s%i", cmpName.c_str(), res.level);
			double csize = (double)res.size;
			double ctime = res.cmpTime;
			double dtime = res.decTime;
			double ratio = rawSize / csize;
			double cspeed = rawSize / ctime;
			double dspeed = rawSize / dtime;
			printf("%12s %7.3f %7.3f %7.3f %7.3f %7.3f %7.3f\n", nameBuf, csize/ oneGB, ctime, dtime, ratio, cspeed/oneGB, dspeed/oneGB);
		}
	}

	// cleanup
	g_Compressors.clear();
}

static bool ReadPlyFile(const char* path, std::vector<float>& dst, size_t& outVertexCount, size_t& outStride)
{
	FILE* f = fopen(path, "rb");
	if (f == nullptr)
	{
		printf("ERROR: failed to open data file %s\n", path);
		return false;
	}
	// read header
	int vertexCount = 0;
	int vertexStride = 0;
	char lineBuf[1024], propType[1024], propName[1024];
	while (true)
	{
		lineBuf[0] = 0;
		fgets(lineBuf, sizeof(lineBuf), f);
		if (0 == strncmp(lineBuf, "end_header", 10))
			break;
		// parse vertex count
		if (1 == sscanf(lineBuf, "element vertex %i", &vertexCount))
		{
			// ok
		}
		// property
		propType[0] = 0;
		propName[0] = 0;
		if (2 == sscanf(lineBuf, "property %s %s", propType, propName))
		{
			if (0 == strcmp(propType, "float")) vertexStride += 4;
			if (0 == strcmp(propType, "double")) vertexStride += 8;
			if (0 == strcmp(propType, "uchar")) vertexStride += 1;
		}
	}

	//printf("PLY file %s: %i verts, %i stride\n", path, vertexCount, vertexStride);

	const size_t kStride = 248;
	if (vertexStride != kStride)
	{
		printf("ERROR: expect vertex stride %zi, file %s had %i\n", kStride, path, vertexStride);
		return false;
	}
	dst.resize(vertexCount * kStride / 4);
	fread(dst.data(), kStride, vertexCount, f);

	outVertexCount = vertexCount;
	outStride = vertexStride;
	return true;
}

int main()
{
	stm_setup();
	printf("CPU: '%s' Compiler: '%s'\n", SysInfoGetCpuName().c_str(), SysInfoGetCompilerName().c_str());

	TestFile testFiles[] = {
		{"synthetic", "../../../../../Assets/Models~/synthetic/point_cloud/iteration_7000/point_cloud.ply"},
		{"bicycle_crop", "../../../../../Assets/Models~/bicycle_cropped/point_cloud/iteration_7000/point_cloud.ply"},
		//{"bicycle_7k", "../../../../../Assets/Models~/bicycle/point_cloud/iteration_7000/point_cloud.ply"},
		//{"bicycle_30k", "../../../../../Assets/Models~/bicycle/point_cloud/iteration_30000/point_cloud.ply"},
		//{"truck_7k", "../../../../../Assets/Models~/truck/point_cloud/iteration_7000/point_cloud.ply"},
	};
	for (auto& tf : testFiles)
	{
		if (!ReadPlyFile(tf.path, tf.fileData, tf.vertexCount, tf.vertexStride))
			return 1;
	}
	TestCompressors(std::size(testFiles), testFiles);
	return 0;
}
