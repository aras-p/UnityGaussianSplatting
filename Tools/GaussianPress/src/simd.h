#pragma once
#include <stdint.h>

#if defined(__x86_64__) || defined(_M_X64)
#	define CPU_ARCH_X64 1
#	include <emmintrin.h> // sse2
#	include <tmmintrin.h> // sse3
#	include <smmintrin.h> // sse4.1
#elif defined(__aarch64__) || defined(_M_ARM64)
#	define CPU_ARCH_ARM64 1
#	include <arm_neon.h>
#else
#   error Unsupported platform (SSE/NEON required)
#endif


#if CPU_ARCH_X64
typedef __m128i Bytes16;
inline Bytes16 SimdZero() { return _mm_setzero_si128(); }
inline Bytes16 SimdSet1(uint8_t v) { return _mm_set1_epi8(v); }
inline Bytes16 SimdLoad(const void* ptr) { return _mm_loadu_si128((const __m128i*)ptr); }
inline Bytes16 SimdLoadA(const void* ptr) { return _mm_load_si128((const __m128i*)ptr); }
inline void SimdStore(void* ptr, Bytes16 x) { _mm_storeu_si128((__m128i*)ptr, x); }
inline void SimdStoreA(void* ptr, Bytes16 x) { _mm_store_si128((__m128i*)ptr, x); }

template<int lane> inline uint8_t SimdGetLane(Bytes16 x) { return _mm_extract_epi8(x, lane); }
template<int lane> inline Bytes16 SimdSetLane(Bytes16 x, uint8_t v) { return _mm_insert_epi8(x, v, lane); }
template<int index> inline Bytes16 SimdConcat(Bytes16 hi, Bytes16 lo) { return _mm_alignr_epi8(hi, lo, index); }

inline Bytes16 SimdAdd(Bytes16 a, Bytes16 b) { return _mm_add_epi8(a, b); }
inline Bytes16 SimdSub(Bytes16 a, Bytes16 b) { return _mm_sub_epi8(a, b); }

inline Bytes16 SimdShuffle(Bytes16 x, Bytes16 table) { return _mm_shuffle_epi8(x, table); }
inline Bytes16 SimdInterleaveL(Bytes16 a, Bytes16 b) { return _mm_unpacklo_epi8(a, b); }
inline Bytes16 SimdInterleaveR(Bytes16 a, Bytes16 b) { return _mm_unpackhi_epi8(a, b); }
inline Bytes16 SimdInterleave4L(Bytes16 a, Bytes16 b) { return _mm_unpacklo_epi32(a, b); }
inline Bytes16 SimdInterleave4R(Bytes16 a, Bytes16 b) { return _mm_unpackhi_epi32(a, b); }

inline Bytes16 SimdPrefixSum(Bytes16 x)
{
    // Sklansky-style sum from https://gist.github.com/rygorous/4212be0cd009584e4184e641ca210528
    x = _mm_add_epi8(x, _mm_slli_epi64(x, 8));
    x = _mm_add_epi8(x, _mm_slli_epi64(x, 16));
    x = _mm_add_epi8(x, _mm_slli_epi64(x, 32));
    x = _mm_add_epi8(x, _mm_shuffle_epi8(x, _mm_setr_epi8(-1,-1,-1,-1,-1,-1,-1,-1,7,7,7,7,7,7,7,7)));
    return x;
}

#elif CPU_ARCH_ARM64
typedef uint8x16_t Bytes16;
inline Bytes16 SimdZero() { return vdupq_n_u8(0); }
inline Bytes16 SimdSet1(uint8_t v) { return vdupq_n_u8(v); }
inline Bytes16 SimdLoad(const void* ptr) { return vld1q_u8((const uint8_t*)ptr); }
inline Bytes16 SimdLoadA(const void* ptr) { return vld1q_u8((const uint8_t*)ptr); }
inline void SimdStore(void* ptr, Bytes16 x) { vst1q_u8((uint8_t*)ptr, x); }
inline void SimdStoreA(void* ptr, Bytes16 x) { vst1q_u8((uint8_t*)ptr, x); }

template<int lane> inline uint8_t SimdGetLane(Bytes16 x) { return vgetq_lane_u8(x, lane); }
template<int lane> inline Bytes16 SimdSetLane(Bytes16 x, uint8_t v) { return vsetq_lane_u8(v, x, lane); }
template<int index> inline Bytes16 SimdConcat(Bytes16 hi, Bytes16 lo) { return vextq_u8(lo, hi, index); }

inline Bytes16 SimdAdd(Bytes16 a, Bytes16 b) { return vaddq_u8(a, b); }
inline Bytes16 SimdSub(Bytes16 a, Bytes16 b) { return vsubq_u8(a, b); }

inline Bytes16 SimdShuffle(Bytes16 x, Bytes16 table) { return vqtbl1q_u8(x, table); }
inline Bytes16 SimdInterleaveL(Bytes16 a, Bytes16 b) { return vzip1q_u8(a, b); }
inline Bytes16 SimdInterleaveR(Bytes16 a, Bytes16 b) { return vzip2q_u8(a, b); }
inline Bytes16 SimdInterleave4L(Bytes16 a, Bytes16 b) { return vreinterpretq_u8_u32(vzip1q_u32(vreinterpretq_u32_u8(a), vreinterpretq_u32_u8(b))); }
inline Bytes16 SimdInterleave4R(Bytes16 a, Bytes16 b) { return vreinterpretq_u8_u32(vzip2q_u32(vreinterpretq_u32_u8(a), vreinterpretq_u32_u8(b))); }


inline Bytes16 SimdPrefixSum(Bytes16 x)
{
    // Kogge-Stone-style like commented out part of https://gist.github.com/rygorous/4212be0cd009584e4184e641ca210528
    Bytes16 zero = vdupq_n_u8(0);
    x = vaddq_u8(x, vextq_u8(zero, x, 16 - 1));
    x = vaddq_u8(x, vextq_u8(zero, x, 16 - 2));
    x = vaddq_u8(x, vextq_u8(zero, x, 16 - 4));
    x = vaddq_u8(x, vextq_u8(zero, x, 16 - 8));
    return x;
}

#endif
