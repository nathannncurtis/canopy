#include "architecture_dispatch.h"
#include "cpu_features.h"
#include "../asm/simd_sum.h"

SmonSumPath SmonGetSumPath()
{
#if defined(_M_ARM64) || defined(__aarch64__)
    return SmonSumPath::Arm64Intrinsic;
#elif defined(SMON_HAS_X64_ASM) && defined(SMON_ENABLE_AVX2_SUM)
    return CpuHasAvx2() ? SmonSumPath::X64Avx2Assembly : SmonSumPath::Scalar;
#else
    return SmonSumPath::Scalar;
#endif
}

uint64_t SmonSumU64_Architecture(const uint64_t* data, uint32_t count)
{
    switch (SmonGetSumPath()) {
#if defined(SMON_HAS_X64_ASM) && defined(SMON_ENABLE_AVX2_SUM)
    case SmonSumPath::X64Avx2Assembly: return SmonSumU64_AVX2(data, count);
#endif
    case SmonSumPath::Arm64Intrinsic: return SmonSumU64_ARM64(data, count);
    default: return SmonSumU64_Scalar(data, count);
    }
}
