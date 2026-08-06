#include "../include/smon_api.h"
#include <cstdio>
#include <cwchar>

static bool Check(bool condition, const wchar_t* message)
{
    if (condition) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

static bool EndsWithInsensitive(const wchar_t* value, uint32_t length,
                                const wchar_t* suffix)
{
    const size_t suffix_length = wcslen(suffix);
    return length >= suffix_length &&
           _wcsnicmp(value + length - suffix_length, suffix, suffix_length) == 0;
}

int wmain(int argc, wchar_t* argv[])
{
    if (!Check(Smon_GetAbiVersion() == SMON_ABI_VERSION, L"ABI version") ||
        !Check(Smon_GetCapabilities(nullptr) == FALSE, L"null capabilities rejected"))
        return 1;
    SmonCapabilities too_small{};
    too_small.struct_size = 8;
    if (!Check(Smon_GetCapabilities(&too_small) == FALSE, L"small capabilities rejected"))
        return 1;
    SmonCapabilities old_caller{};
    old_caller.struct_size = 16;
    if (!Check(Smon_GetCapabilities(&old_caller) != FALSE, L"older capability prefix accepted") ||
        !Check(old_caller.struct_size == sizeof(SmonCapabilities), L"current capability size reported"))
        return 1;
    SmonCapabilities capabilities{};
    capabilities.struct_size = sizeof(capabilities);
    if (!Check(Smon_GetCapabilities(&capabilities) != FALSE, L"capabilities returned") ||
        !Check(capabilities.abi_version == SMON_ABI_VERSION, L"capability ABI version") ||
        !Check((capabilities.flags & SMON_CAP_DIRECTORY_SCANNER) != 0,
               L"directory capability") ||
        !Check((capabilities.flags & SMON_CAP_SCAN_OPTIONS) != 0,
               L"scan options capability") ||
        !Check((capabilities.flags & SMON_CAP_ERROR_INFO) != 0,
               L"structured error capability") ||
        !Check(capabilities.max_nodes > 0 && capabilities.max_name_bytes > 0,
               L"arena capabilities"))
        return 1;

    if (!Check(Smon_Cancel(nullptr) == FALSE, L"cancel rejects null") ||
        !Check(Smon_SetPaused(nullptr, TRUE) == FALSE, L"pause rejects null") ||
        !Check(Smon_Wait(nullptr, 0) == FALSE, L"wait rejects null") ||
        !Check(Smon_GetError(nullptr) == ERROR_INVALID_HANDLE, L"null error code") ||
        !Check(Smon_GetErrorInfo(nullptr, nullptr) == FALSE, L"null error info rejected") ||
        !Check(Smon_GetScannerKind(nullptr) == SMON_SCANNER_UNKNOWN, L"null scanner kind"))
        return 1;

    const wchar_t* path = argc > 1 ? argv[1] : L"C:\\Windows\\System32";
    wprintf(L"scan_test: %s\n", path);

    if (!Check(Smon_BeginScanEx(nullptr, nullptr, nullptr, nullptr) == nullptr &&
               GetLastError() == ERROR_INVALID_PARAMETER,
               L"extended scan rejects null path"))
        return 1;

    SmonScanOptions invalid{};
    invalid.struct_size = sizeof(invalid) - 1;
    if (!Check(Smon_BeginScanEx(path, &invalid, nullptr, nullptr) == nullptr &&
               GetLastError() == ERROR_INSUFFICIENT_BUFFER,
               L"extended scan rejects a short options struct"))
        return 1;

    invalid = {};
    invalid.struct_size = sizeof(invalid);
    invalid.flags = 0x80000000u;
    if (!Check(Smon_BeginScanEx(path, &invalid, nullptr, nullptr) == nullptr &&
               GetLastError() == ERROR_INVALID_FLAGS,
               L"extended scan rejects unknown flags"))
        return 1;

    invalid = {};
    invalid.struct_size = sizeof(invalid);
    invalid.minimum_file_size = 2;
    invalid.maximum_file_size = 1;
    if (!Check(Smon_BeginScanEx(path, &invalid, nullptr, nullptr) == nullptr &&
               GetLastError() == ERROR_INVALID_PARAMETER,
               L"extended scan rejects an inverted size range"))
        return 1;

    invalid = {};
    invalid.struct_size = sizeof(invalid);
    invalid.worker_threads = 33;
    if (!Check(Smon_BeginScanEx(path, &invalid, nullptr, nullptr) == nullptr &&
               GetLastError() == ERROR_INVALID_PARAMETER,
               L"extended scan rejects an excessive worker count"))
        return 1;

    SmonScanOptions filtered{};
    filtered.struct_size = sizeof(filtered);
    filtered.worker_threads = 1;
    filtered.excluded_extensions = L".cpp";
    ScanHandle filtered_handle = Smon_BeginScanEx(path, &filtered, nullptr, nullptr);
    if (!Check(filtered_handle != nullptr, L"extended scan starts"))
        return 1;
    if (!Check(Smon_GetScannerKind(filtered_handle) == SMON_SCANNER_DIRECTORY,
               L"constraining options route to directory scanner") ||
        !Check(Smon_Wait(filtered_handle, INFINITE) != FALSE,
               L"filtered scan completed")) {
        Smon_Cancel(filtered_handle);
        Smon_Wait(filtered_handle, INFINITE);
        Smon_FreeResult(filtered_handle);
        return 1;
    }
    ScanResult filtered_result{};
    if (!Check(Smon_GetResult(filtered_handle, &filtered_result) != FALSE,
               L"filtered result returned")) {
        Smon_FreeResult(filtered_handle);
        return 1;
    }
    for (uint32_t i = 0; i < filtered_result.node_count; ++i) {
        const ScanNode& node = filtered_result.nodes[i];
        const wchar_t* name = filtered_result.name_buf + node.name_offset;
        if (!Check(!EndsWithInsensitive(name, node.name_len, L".cpp"),
                   L"excluded extension absent from result")) {
            Smon_FreeResult(filtered_handle);
            return 1;
        }
    }
    Smon_FreeResult(filtered_handle);

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
    SmonErrorInfo error_info{};
    error_info.struct_size = sizeof(error_info);
    if (!Check(Smon_GetErrorInfo(h, &error_info) != FALSE, L"structured error info returned") ||
        !Check(error_info.struct_size == sizeof(error_info), L"structured error size returned")) {
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
