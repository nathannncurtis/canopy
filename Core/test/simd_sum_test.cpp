#include "../asm/simd_sum.h"
#include "../src/cpu_features.h"
#include <cstdio>
#include <vector>

int wmain()
{
    if (!CpuHasAvx2()) {
        wprintf(L"AVX2 unavailable; assembly parity test skipped.\n");
        return 0;
    }

    std::vector<uint64_t> values(259);
    for (size_t i = 0; i < values.size(); ++i)
        values[i] = (static_cast<uint64_t>(i) * 0x9E3779B185EBCA87ull) ^ (i << 17);

    // Start at element one to exercise unaligned loads. Every vector/tail boundary
    // through the four-accumulator loop is covered.
    const uint64_t* data = values.data() + 1;
    for (uint32_t count = 0; count <= 257; ++count) {
        uint64_t expected = 0;
        for (uint32_t i = 0; i < count; ++i) expected += data[i];
        uint64_t actual = SmonSumU64_AVX2(data, count);
        if (actual != expected) {
            fwprintf(stderr, L"FAIL: count=%u expected=%llu actual=%llu\n",
                     count, expected, actual);
            return 1;
        }
    }
    return 0;
}
