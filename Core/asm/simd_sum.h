#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

uint64_t SmonSumU64_AVX2(const uint64_t* data, uint32_t count);

#ifdef __cplusplus
}
#endif
