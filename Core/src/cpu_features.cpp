#include "cpu_features.h"
#if defined(_M_IX86) || defined(_M_X64)
#include <intrin.h>
#endif

bool CpuHasAvx2()
{
#if defined(_M_IX86) || defined(_M_X64)
    int registers[4] = {};
    __cpuid(registers, 0);
    if (registers[0] < 7) return false;

    __cpuid(registers, 1);
    constexpr int kOsXsave = 1 << 27;
    constexpr int kAvx = 1 << 28;
    if ((registers[2] & (kOsXsave | kAvx)) != (kOsXsave | kAvx)) return false;
    if ((_xgetbv(0) & 0x6) != 0x6) return false; // XMM and YMM state enabled by the OS

    __cpuidex(registers, 7, 0);
    return (registers[1] & (1 << 5)) != 0;
#else
    return false;
#endif
}
