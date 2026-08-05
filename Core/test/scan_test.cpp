#include "../include/smon_api.h"
#include <cstdio>

static bool Check(bool condition, const wchar_t* message)
{
    if (condition) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

int wmain(int argc, wchar_t* argv[])
{
    if (!Check(Smon_GetAbiVersion() == SMON_ABI_VERSION, L"ABI version") ||
        !Check(Smon_GetCapabilities(nullptr) == FALSE, L"null capabilities rejected"))
        return 1;
    SmonCapabilities too_small{};
    too_small.struct_size = sizeof(too_small) - 1;
    if (!Check(Smon_GetCapabilities(&too_small) == FALSE, L"small capabilities rejected"))
        return 1;
    SmonCapabilities capabilities{};
    capabilities.struct_size = sizeof(capabilities);
    if (!Check(Smon_GetCapabilities(&capabilities) != FALSE, L"capabilities returned") ||
        !Check(capabilities.abi_version == SMON_ABI_VERSION, L"capability ABI version") ||
        !Check((capabilities.flags & SMON_CAP_DIRECTORY_SCANNER) != 0,
               L"directory capability") ||
        !Check(capabilities.max_nodes > 0 && capabilities.max_name_bytes > 0,
               L"arena capabilities"))
        return 1;

    if (!Check(Smon_Cancel(nullptr) == FALSE, L"cancel rejects null") ||
        !Check(Smon_SetPaused(nullptr, TRUE) == FALSE, L"pause rejects null") ||
        !Check(Smon_Wait(nullptr, 0) == FALSE, L"wait rejects null") ||
        !Check(Smon_GetError(nullptr) == ERROR_INVALID_HANDLE, L"null error code") ||
        !Check(Smon_GetScannerKind(nullptr) == SMON_SCANNER_UNKNOWN, L"null scanner kind"))
        return 1;

    const wchar_t* path = argc > 1 ? argv[1] : L"C:\\Windows\\System32";
    wprintf(L"scan_test: %s\n", path);

    ScanHandle h = Smon_BeginScan(path, nullptr, nullptr);
    if (!h) { wprintf(L"Smon_BeginScan returned null\n"); return 1; }

    if (!Check(Smon_GetScannerKind(h) != SMON_SCANNER_UNKNOWN, L"scanner kind selected") ||
        !Check(Smon_SetPaused(h, TRUE) != FALSE, L"pause accepted") ||
        !Check(Smon_SetPaused(h, FALSE) != FALSE, L"resume accepted") ||
        !Check(Smon_Wait(h, INFINITE) != FALSE, L"scan completed")) {
        Smon_Cancel(h);
        Smon_Wait(h, INFINITE);
        Smon_FreeResult(h);
        return 1;
    }

    ScanResult r{};
    if (!Smon_GetResult(h, &r)) {
        wprintf(L"Smon_GetResult failed: %lu\n", Smon_GetError(h));
        Smon_FreeResult(h);
        return 1;
    }
    wprintf(L"scanner=%lu  nodes=%u  total=%llu bytes\n",
            Smon_GetScannerKind(h), r.node_count, r.total_bytes);
    if (!Check(r.node_count > 0, L"scan has a deterministic root") ||
        !Check(r.nodes[0].parent == UINT32_MAX, L"node zero is the root")) {
        Smon_FreeResult(h);
        return 1;
    }
    for (uint32_t i = 1; i < r.node_count; ++i) {
        if (!Check(r.nodes[i].parent < i, L"parents precede descendants")) {
            Smon_FreeResult(h);
            return 1;
        }
    }

    Smon_FreeResult(h);
    return 0;
}
