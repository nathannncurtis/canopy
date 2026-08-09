#pragma once
#include <stdint.h>

enum class SmonSumPath : uint32_t { Scalar = 0, X64Avx2Assembly = 1, Arm64Intrinsic = 2 };

SmonSumPath SmonGetSumPath();
uint64_t SmonSumU64_Architecture(const uint64_t* data, uint32_t count);
