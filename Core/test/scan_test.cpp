#include "../include/smon_api.h"
#include <cstdio>
#include <cwchar>
#include <cstddef>

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
        !Check((capabilities.flags & SMON_CAP_SCAN_TELEMETRY) != 0,
               L"scan telemetry capability") ||
#if defined(_M_ARM64)
        !Check((capabilities.flags & SMON_CAP_ARM64_INTRINSICS) != 0,
               L"ARM64 intrinsic capability") ||
        !Check((capabilities.flags & SMON_CAP_AVX2_ASM) == 0,
               L"ARM64 build excludes x64 assembly capability") ||
#endif
        !Check(capabilities.max_nodes > 0 && capabilities.max_name_bytes > 0,
               L"arena capabilities"))
        return 1;

    if (!Check(Smon_Cancel(nullptr) == FALSE, L"cancel rejects null") ||
        !Check(Smon_SetPaused(nullptr, TRUE) == FALSE, L"pause rejects null") ||
        !Check(Smon_Wait(nullptr, 0) == FALSE, L"wait rejects null") ||
        !Check(Smon_GetError(nullptr) == ERROR_INVALID_HANDLE, L"null error code") ||
        !Check(Smon_GetErrorInfo(nullptr, nullptr) == FALSE, L"null error info rejected") ||
        !Check(Smon_GetScanStatus(nullptr, nullptr) == FALSE, L"null scan status rejected") ||
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

    SmonScanOptions legacy{};
    legacy.struct_size = static_cast<uint32_t>(offsetof(SmonScanOptions, traversal_policy_version));
    legacy.flags = SMON_OPTION_FORCE_DIRECTORY_SCAN;
    legacy.worker_threads = 1;
    ScanHandle legacy_options_handle = Smon_BeginScanEx(path, &legacy, nullptr, nullptr);
    if (!Check(legacy_options_handle != nullptr, L"extended scan accepts the original options struct"))
        return 1;
    Smon_Cancel(legacy_options_handle);
    Smon_Wait(legacy_options_handle, INFINITE);
    Smon_FreeResult(legacy_options_handle);

    SmonScanOptions traversal_v1{};
    traversal_v1.struct_size = static_cast<uint32_t>(offsetof(SmonScanOptions, network_worker_threads));
    traversal_v1.flags = SMON_OPTION_FORCE_DIRECTORY_SCAN;
    traversal_v1.traversal_policy_version = 1;
    ScanHandle traversal_v1_handle = Smon_BeginScanEx(path, &traversal_v1, nullptr, nullptr);
    if (!Check(traversal_v1_handle != nullptr, L"extended scan accepts traversal v1 options")) return 1;
    Smon_Cancel(traversal_v1_handle);
    Smon_Wait(traversal_v1_handle, INFINITE);
    Smon_FreeResult(traversal_v1_handle);

    invalid = {};
    invalid.struct_size = sizeof(invalid);
    invalid.flags = 0x80000000u;
    if (!Check(Smon_BeginScanEx(path, &invalid, nullptr, nullptr) == nullptr &&
               GetLastError() == ERROR_INVALID_FLAGS,
               L"extended scan rejects unknown flags"))
        return 1;

    invalid = {};
    invalid.struct_size = sizeof(invalid);
    invalid.traversal_policy_version = 2;
    if (!Check(Smon_BeginScanEx(path, &invalid, nullptr, nullptr) == nullptr &&
               GetLastError() == ERROR_INVALID_PARAMETER,
               L"extended scan rejects an unknown traversal policy version")) return 1;
    invalid.traversal_policy_version = 1;
    invalid.reserved = 1;
    if (!Check(Smon_BeginScanEx(path, &invalid, nullptr, nullptr) == nullptr &&
               GetLastError() == ERROR_INVALID_PARAMETER,
               L"extended scan rejects nonzero reserved fields")) return 1;

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
    invalid = {};
    invalid.struct_size = sizeof(invalid);
    invalid.network_worker_threads = 17;
    if (!Check(Smon_BeginScanEx(path, &invalid, nullptr, nullptr) == nullptr &&
               GetLastError() == ERROR_INVALID_PARAMETER,
               L"extended scan rejects excessive network concurrency")) return 1;

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
    SmonRouteInfo short_route{};
    short_route.struct_size = sizeof(uint32_t);
    if (!Check(!Smon_GetRouteInfo(filtered_handle, &short_route) &&
               GetLastError() == ERROR_INSUFFICIENT_BUFFER &&
               short_route.struct_size == sizeof(SmonRouteInfo),
               L"route info reports required additive struct size")) return 1;
    SmonRouteInfo route{};
    route.struct_size = sizeof(route);
    if (!Check(Smon_GetRouteInfo(filtered_handle, &route) != FALSE,
               L"route info is available") ||
        !Check(route.scanner_kind == SMON_SCANNER_DIRECTORY &&
               route.fallback_reason == SMON_ROUTE_CONSTRAINING_OPTIONS,
               L"route info explains directory fallback")) return 1;
    ScanResult filtered_result{};
    if (!Check(Smon_GetResult(filtered_handle, &filtered_result) != FALSE,
               L"filtered result returned")) {
        Smon_FreeResult(filtered_handle);
        return 1;
    }
    SmonNodeMetadata short_metadata{};
    short_metadata.struct_size = sizeof(uint32_t);
    if (!Check(!Smon_GetNodeMetadata(filtered_handle, 0, &short_metadata) &&
               GetLastError() == ERROR_INSUFFICIENT_BUFFER &&
               short_metadata.struct_size == sizeof(SmonNodeMetadata),
               L"node metadata negotiates additive struct size")) return 1;
    SmonNodeMetadata root_metadata{ sizeof(SmonNodeMetadata) };
    if (!Check(Smon_GetNodeMetadata(filtered_handle, 0, &root_metadata),
               L"root metadata query succeeds") ||
        !Check(root_metadata.link_count >= 1, L"root metadata exposes link count")) return 1;
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

    // Exercise the legacy entry point without making the contract depend on the
    // hosted volume's USN-journal policy. Some CI images expose NTFS and elevation
    // but deliberately leave the journal inactive (ERROR_JOURNAL_NOT_ACTIVE).
    ScanHandle legacy_handle = Smon_BeginScan(path, nullptr, nullptr);
    if (!legacy_handle) { wprintf(L"Smon_BeginScan returned null\n"); return 1; }
    Smon_Cancel(legacy_handle);
    Smon_Wait(legacy_handle, INFINITE);
    Smon_FreeResult(legacy_handle);

    SmonScanOptions deterministic{};
    deterministic.struct_size = sizeof(deterministic);
    deterministic.flags = SMON_OPTION_FORCE_DIRECTORY_SCAN;
    deterministic.worker_threads = 1;
    ScanHandle h = Smon_BeginScanEx(path, &deterministic, nullptr, nullptr);
    if (!h) { wprintf(L"deterministic directory scan returned null\n"); return 1; }

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
    SmonScanStatus status{};
    status.struct_size = sizeof(status);
    if (!Check(Smon_GetScanStatus(h, &status) != FALSE, L"scan status returned") ||
        !Check(status.struct_size == sizeof(status), L"scan status size returned") ||
        !Check(status.phase >= SMON_SCAN_PHASE_DISCOVERY &&
               status.phase <= SMON_SCAN_PHASE_FINALIZATION,
               L"pre-result scan phase is valid") ||
        !Check(status.terminal == FALSE, L"pre-result status is not terminal")) {
        Smon_FreeResult(h);
        return 1;
    }

    ScanResult r{};
    if (!Smon_GetResult(h, &r)) {
        wprintf(L"Smon_GetResult failed: %lu\n", Smon_GetError(h));
        Smon_FreeResult(h);
        return 1;
    }
    status = {};
    status.struct_size = sizeof(status);
    if (!Check(Smon_GetScanStatus(h, &status) != FALSE, L"terminal scan status returned") ||
        !Check(status.phase == SMON_SCAN_PHASE_COMPLETE && status.terminal != FALSE,
               L"terminal phase delivered") ||
        !Check(status.dirs_visited + status.files_visited > 0,
               L"telemetry contains visited items")) {
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
