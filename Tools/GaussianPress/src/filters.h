#pragma once

#include <stdint.h>
#include <stddef.h>

// Process 16xN bytes at once,
// based on filter "H" from https://aras-p.info/blog/2023/03/01/Float-Compression-7-More-Filtering-Optimization/
void Filter_ByteDelta(const uint8_t* src, uint8_t* dst, size_t channels, size_t dataElems);
void UnFilter_ByteDelta(const uint8_t* src, uint8_t* dst, size_t channels, size_t dataElems);
