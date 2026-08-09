#include "../asm/simd_sum.h"
#include "../src/cpu_features.h"
#include <cstdio>
#include <algorithm>
#include <array>
#include <chrono>
#include <vector>

__declspec(noinline) static uint64_t ScalarSum(const uint64_t* data, uint32_t count)
{
    uint64_t total = 0;
    for (uint32_t index = 0; index < count; ++index) total += data[index];
    return total;
}

using SumFunction = uint64_t (*)(const uint64_t*, uint32_t);

static double Measure(SumFunction sum, uint64_t* data, uint32_t count, uint32_t repeats,
                      volatile uint64_t& sink)
{
    SumFunction volatile dispatch = sum;
    const auto start = std::chrono::steady_clock::now();
    volatile uint64_t value = 0;
    for (uint32_t repeat = 0; repeat < repeats; ++repeat) {
        const uint32_t offset = repeat & 7u;
        data[offset] ^= 0x9E3779B185EBCA87ull + repeat;
        value = value ^ dispatch(data + offset, count - 8u);
    }
    const auto finish = std::chrono::steady_clock::now();
    sink = sink ^ value;
    return std::chrono::duration<double, std::nano>(finish - start).count() / repeats;
}

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

    // Gate assembly dispatch on a representative, repeated comparison against the
    // optimizing compiler's scalar implementation. Median alternating trials limit
    // scheduler and first-run bias; CI fails if the raw-ASM path does not win.
    constexpr uint32_t benchmark_count = 1u << 20;
    constexpr uint32_t repeats = 32;
    std::vector<uint64_t> benchmark_values(benchmark_count);
    for (uint32_t i = 0; i < benchmark_count; ++i)
        benchmark_values[i] = (static_cast<uint64_t>(i) * 0xD6E8FEB86659FD93ull) ^ (i >> 3);
    volatile uint64_t sink = 0;
    (void)Measure(ScalarSum, benchmark_values.data(), benchmark_count, 2, sink);
    (void)Measure(SmonSumU64_AVX2, benchmark_values.data(), benchmark_count, 2, sink);
    std::array<double, 9> scalar_ns{};
    std::array<double, 9> assembly_ns{};
    for (size_t trial = 0; trial < scalar_ns.size(); ++trial) {
        if ((trial & 1) == 0) {
            scalar_ns[trial] = Measure(ScalarSum, benchmark_values.data(), benchmark_count, repeats, sink);
            assembly_ns[trial] = Measure(SmonSumU64_AVX2, benchmark_values.data(), benchmark_count, repeats, sink);
        } else {
            assembly_ns[trial] = Measure(SmonSumU64_AVX2, benchmark_values.data(), benchmark_count, repeats, sink);
            scalar_ns[trial] = Measure(ScalarSum, benchmark_values.data(), benchmark_count, repeats, sink);
        }
    }
    std::sort(scalar_ns.begin(), scalar_ns.end());
    std::sort(assembly_ns.begin(), assembly_ns.end());
    const double scalar_median = scalar_ns[scalar_ns.size() / 2];
    const double assembly_median = assembly_ns[assembly_ns.size() / 2];
    const double speedup = scalar_median / assembly_median;
    wprintf(L"SIMD benchmark: scalar %.0f ns, raw ASM %.0f ns, %.2fx (sink %llu)\n",
            scalar_median, assembly_median, speedup, static_cast<unsigned long long>(sink));
    const bool measured_win = assembly_median < scalar_median * 0.98;
#if defined(SMON_ENABLE_AVX2_SUM)
    if (!measured_win) {
        fwprintf(stderr, L"FAIL: raw ASM must beat optimized scalar by at least 2%% to remain enabled.\n");
        return 2;
    }
#else
    (void)measured_win;
    wprintf(L"Production AVX2 dispatch is disabled; configure SMON_ENABLE_AVX2_SUM=ON only on targets with a repeatable measured win.\n");
#endif
    return 0;
}
