#include "../src/architecture_dispatch.h"
#include "../asm/simd_sum.h"
#include <cstdio>
#include <vector>

int wmain()
{
    std::vector<uint64_t> values(260);
    for (size_t index = 0; index < values.size(); ++index)
        values[index] = static_cast<uint64_t>(index) * 0x9E3779B185EBCA87ull;
    const uint64_t* unaligned = values.data() + 1;
    for (uint32_t count = 0; count <= 257; ++count) {
        const uint64_t expected = SmonSumU64_Scalar(unaligned, count);
        const uint64_t actual = SmonSumU64_Architecture(unaligned, count);
        if (actual != expected) {
            fwprintf(stderr, L"FAIL: architecture sum count=%u expected=%llu actual=%llu\n",
                count, expected, actual);
            return 1;
        }
    }
#if defined(_M_ARM64)
    if (SmonGetSumPath() != SmonSumPath::Arm64Intrinsic) {
        fwprintf(stderr, L"FAIL: native ARM64 build did not select ARM64 intrinsics.\n");
        return 2;
    }
#endif
    wprintf(L"Architecture sum parity passed; dispatch path=%u.\n",
        static_cast<unsigned>(SmonGetSumPath()));
    return 0;
}
