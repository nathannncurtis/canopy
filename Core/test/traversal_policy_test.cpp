#include "../include/smon_api.h"
#include "../src/traversal_policy.h"
#include <cstdio>
#include <filesystem>
#include <string>

static bool Check(bool value, const wchar_t* message)
{
    if (value) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

struct OwnedScan {
    ScanHandle handle = nullptr;
    ScanResult result{};
    SmonScanStatus status{};
    DWORD error = ERROR_SUCCESS;
    ~OwnedScan() { if (handle) Smon_FreeResult(handle); }
    OwnedScan() = default;
    OwnedScan(const OwnedScan&) = delete;
    OwnedScan& operator=(const OwnedScan&) = delete;
};

static bool Scan(const std::wstring& root, uint32_t flags, OwnedScan& scan)
{
    SmonScanOptions options{};
    options.struct_size = sizeof(options);
    options.flags = flags | SMON_OPTION_FORCE_DIRECTORY_SCAN;
    options.worker_threads = 1;
    options.traversal_policy_version = 1;
    scan.handle = Smon_BeginScanEx(root.c_str(), &options, nullptr, nullptr);
    if (!scan.handle) { scan.error = GetLastError(); return false; }
    if (!Smon_Wait(scan.handle, 10000)) return false;
    scan.error = Smon_GetError(scan.handle);
    scan.status.struct_size = sizeof(scan.status);
    if (!Smon_GetScanStatus(scan.handle, &scan.status)) return false;
    return Smon_GetResult(scan.handle, &scan.result) != FALSE;
}

static const ScanNode* FindFlag(const ScanResult& result, uint32_t flag)
{
    for (uint32_t i = 0; i < result.node_count; ++i)
        if ((result.nodes[i].flags & flag) != 0) return &result.nodes[i];
    return nullptr;
}

static bool HasName(const ScanResult& result, std::wstring_view expected)
{
    for (uint32_t i = 0; i < result.node_count; ++i) {
        const ScanNode& node = result.nodes[i];
        const wchar_t* name = reinterpret_cast<const wchar_t*>(
            reinterpret_cast<const BYTE*>(result.name_buf) + node.name_offset);
        if (std::wstring_view(name, node.name_len) == expected) return true;
    }
    return false;
}

int wmain()
{
    TraversalIdentityTracker tracker;
    if (!Check(!tracker.HasRootVolume(), L"tracker root starts uninitialized") ||
        !Check(tracker.Observe(0, 10, true) == TraversalDecision::Visit,
               L"zero is a valid initialized root serial") ||
        !Check(tracker.HasRootVolume() && tracker.RootVolumeSerial() == 0,
               L"zero root serial is retained explicitly") ||
        !Check(tracker.Observe(0, 11, true) == TraversalDecision::Visit,
               L"same-volume child is visited") ||
        !Check(tracker.Observe(0, 10, true) == TraversalDecision::AlreadyVisited,
               L"same identity is a cycle") ||
        !Check(tracker.Observe(7, 12, true) == TraversalDecision::DifferentVolume,
               L"mounted volume is rejected by default") ||
        !Check(tracker.Observe(7, 12, false) == TraversalDecision::Visit,
               L"cross-volume opt-out permits the mounted identity")) return 1;

    wchar_t temp[MAX_PATH]{};
    if (!GetTempPathW(MAX_PATH, temp)) return 1;
    std::wstring root = std::wstring(temp) + L"canopy-policy-" + std::to_wstring(GetCurrentProcessId());
    std::filesystem::remove_all(root);
    std::filesystem::create_directories(root + L"\\data");
    std::wstring file = root + L"\\data\\payload.bin";
    HANDLE base = CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (base == INVALID_HANDLE_VALUE) return 1;
    DWORD written = 0;
    const char payload[] = "base";
    WriteFile(base, payload, sizeof(payload), &written, nullptr);
    CloseHandle(base);

    bool ads_supported = false;
    constexpr char stream_payload[] = "named-stream";
    HANDLE stream = CreateFileW((file + L":canopy-test").c_str(), GENERIC_WRITE, 0, nullptr,
        CREATE_ALWAYS, 0, nullptr);
    if (stream != INVALID_HANDLE_VALUE) {
        ads_supported = WriteFile(stream, stream_payload, sizeof(stream_payload), &written, nullptr) != FALSE;
        CloseHandle(stream);
    }

    {
        OwnedScan defaults;
        if (!Check(Scan(root, 0, defaults), L"default policy scan completes") ||
            !Check(FindFlag(defaults.result, SMON_FLAG_STREAM) == nullptr,
                   L"named streams excluded by default")) return 1;
    }

    if (ads_supported) {
        OwnedScan with_streams;
        if (!Check(Scan(root, SMON_OPTION_INCLUDE_STREAMS, with_streams),
                   L"named-stream scan completes")) return 1;
        const ScanNode* stream_node = FindFlag(with_streams.result, SMON_FLAG_STREAM);
        if (!Check(stream_node != nullptr, L"named stream reported explicitly") ||
            !Check(stream_node->parent != UINT32_MAX, L"stream has its file parent") ||
            !Check(stream_node->size == sizeof(stream_payload), L"stream exposes logical byte size") ||
            !Check(std::wstring_view(reinterpret_cast<const wchar_t*>(
                       reinterpret_cast<const BYTE*>(with_streams.result.name_buf) + stream_node->name_offset),
                       stream_node->name_len) == L":canopy-test", L"stream name is preserved") ||
            !Check(with_streams.status.files_visited == 2,
                   L"file telemetry includes the base file and named stream") ||
            !Check(with_streams.status.bytes_seen >= sizeof(payload) + sizeof(stream_payload),
                   L"byte telemetry includes named-stream logical bytes")) return 1;
    }

    std::wstring deep = L"\\\\?\\" + root;
    while (deep.size() < 280) {
        deep += L"\\segment-0123456789";
        if (!CreateDirectoryW(deep.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) return 1;
    }
    std::wstring deep_file = deep + L"\\deep.bin";
    HANDLE deep_handle = CreateFileW(deep_file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (deep_handle == INVALID_HANDLE_VALUE) return 1;
    CloseHandle(deep_handle);
    {
        OwnedScan long_paths;
        if (!Check(Scan(root, 0, long_paths), L"long-path scan completes") ||
            !Check(HasName(long_paths.result, L"deep.bin"), L"path beyond MAX_PATH is not truncated")) return 1;
    }

    bool link_created = CreateSymbolicLinkW((root + L"\\data\\cycle").c_str(), root.c_str(),
        SYMBOLIC_LINK_FLAG_DIRECTORY | 0x2) != FALSE;
    if (link_created) {
        OwnedScan report_only;
        if (!Check(Scan(root, 0, report_only), L"report-only reparse scan completes") ||
            !Check(FindFlag(report_only.result, SMON_FLAG_REPARSE) != nullptr,
                   L"reparse point is reported")) return 1;
        OwnedScan followed;
        if (!Check(Scan(root, SMON_OPTION_FOLLOW_REPARSE, followed),
                   L"cycle-safe follow scan completes") ||
            !Check(followed.result.node_count < 32,
                   L"reparse cycle does not recurse indefinitely")) return 1;
    }

    std::filesystem::remove_all(L"\\\\?\\" + root);
    return 0;
}
