#include "systeminfo.h"

#ifdef _WIN32
#include <intrin.h>
#include <windows.h>
#endif
#ifdef __APPLE__
#include <sys/sysctl.h>
#endif

static std::string TrimRight(std::string s)
{
	while (!s.empty())
	{
		if (s.back() <= ' ')
			s.pop_back();
		else
			break;
	}
	return s;
}

std::string SysInfoGetCpuName()
{
#	if defined(_WIN32)
	// Windows:
	int cpuInfo[4] = { -1 };
	char brandString[49] = {};

	__cpuid(cpuInfo, 0x80000002);
	memcpy(brandString, cpuInfo, 16);
	__cpuid(cpuInfo, 0x80000003);
	memcpy(brandString + 16, cpuInfo, 16);
	__cpuid(cpuInfo, 0x80000004);
	memcpy(brandString + 32, cpuInfo, 16);
	return TrimRight(brandString);

#	elif defined(__APPLE__)
	// macOS:
	char buffer[256];
	size_t bufferLen = sizeof(buffer);
	sysctlbyname("machdep.cpu.brand_string", &buffer, &bufferLen, NULL, 0);
	return TrimRight(buffer);

#	else
#	error Unknown platform
#	endif
}


std::string SysInfoGetCompilerName()
{
#if defined __clang__
	// Clang
	char buf[256];
	snprintf(buf, sizeof(buf), "Clang %i.%i", __clang_major__, __clang_minor__);
	return buf;
#elif defined _MSC_VER
	// MSVC
#	if _MSC_VER >= 1937
	return "MSVC 2022 17.7+";
#	elif _MSC_VER == 1936
	return "MSVC 2022 17.6";
#	elif _MSC_VER == 1935
	return "MSVC 2022 17.5";
#	elif _MSC_VER == 1934
	return "MSVC 2022 17.4";
#	elif _MSC_VER == 1933
	return "MSVC 2022 17.3";
#	elif _MSC_VER == 1932
	return "MSVC 2022 17.2";
#	elif _MSC_VER == 1931
	return "MSVC 2022 17.1";
#	elif _MSC_VER == 1930
	return "MSVC 2022 17.0";
#	elif _MSC_VER == 1929
	return "MSVC 2019 16.10+";
#	elif _MSC_VER == 1928
	return "MSVC 2019 16.8/9";
#	elif _MSC_VER == 1927
	return "MSVC 2019 16.7";
#	elif _MSC_VER == 1926
	return "MSVC 2019 16.6";
#	elif _MSC_VER == 1925
	return "MSVC 2019 16.5";
#	elif _MSC_VER == 1924
	return "MSVC 2019 16.4";
#	elif _MSC_VER == 1923
	return "MSVC 2019 16.3";
#	elif _MSC_VER == 1922
	return "MSVC 2019 16.2";
#	elif _MSC_VER == 1921
	return "MSVC 2019 16.1";
#	elif _MSC_VER == 1920
	return "MSVC 2019 16.0";
#	elif _MSC_VER >= 1910
	return "MSVC 2017";
#	elif _MSC_VER >= 1900
	return "MSVC 2015";
#	else
	return "MSVC Unknown";
#	endif
#else
#	error Unknown compiler
#endif
}

const size_t kCacheFlushDataSize = 128 * 1024 * 1024;
static uint64_t s_CacheFlushArray[kCacheFlushDataSize / 8];
static uint64_t s_CacheFlushScramble;

void SysInfoFlushCaches()
{
#	ifdef WIN32
	FlushInstructionCache(GetCurrentProcess(), NULL, 0);
#	endif
	for (size_t i = 0; i < kCacheFlushDataSize / 8; ++i)
	{
		s_CacheFlushArray[i] = i + s_CacheFlushScramble;
	}
	s_CacheFlushScramble = s_CacheFlushArray[kCacheFlushDataSize / 137];
}
