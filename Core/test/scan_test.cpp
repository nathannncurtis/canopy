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
    if (Smon_GetResult(h, &r))
        wprintf(L"nodes=%u  total=%llu bytes\n", r.node_count, r.total_bytes);
    else
        wprintf(L"Smon_GetResult failed\n");

    Smon_FreeResult(h);
    return 0;
}
