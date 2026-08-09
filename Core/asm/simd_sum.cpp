#include "simd_sum.h"

#if defined(_M_ARM64)
#include <arm64_neon.h>
#elif defined(__aarch64__)
#include <arm_neon.h>
#endif

uint64_t SmonSumU64_Scalar(const uint64_t* data, uint32_t count)
{
    uint64_t total = 0;
    for (uint32_t index = 0; index < count; ++index) total += data[index];
    return total;
}

uint64_t SmonSumU64_ARM64(const uint64_t* data, uint32_t count)
{
#if defined(_M_ARM64) || defined(__aarch64__)
    uint64x2_t sum = vdupq_n_u64(0);
    uint32_t index = 0;
    for (; index + 2 <= count; index += 2) sum = vaddq_u64(sum, vld1q_u64(data + index));
    uint64_t total = vgetq_lane_u64(sum, 0) + vgetq_lane_u64(sum, 1);
    for (; index < count; ++index) total += data[index];
    return total;
#else
    return SmonSumU64_Scalar(data, count);
#endif
}
