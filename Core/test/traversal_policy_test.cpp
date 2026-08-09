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

static uint32_t FindNameIndex(const ScanResult& result, std::wstring_view expected)
{
    for (uint32_t i = 0; i < result.node_count; ++i) {
        const ScanNode& node = result.nodes[i];
        const wchar_t* name = reinterpret_cast<const wchar_t*>(
            reinterpret_cast<const BYTE*>(result.name_buf) + node.name_offset);
        if (std::wstring_view(name, node.name_len) == expected) return i;
    }
    return UINT32_MAX;
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

    std::wstring case_dir = root + L"\\case-sensitive";
    CreateDirectoryW(case_dir.c_str(), nullptr);
    HANDLE case_handle = CreateFileW(case_dir.c_str(), FILE_WRITE_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    bool case_supported = false;
    if (case_handle != INVALID_HANDLE_VALUE) {
        FILE_CASE_SENSITIVE_INFO case_info{ FILE_CS_FLAG_CASE_SENSITIVE_DIR };
        case_supported = SetFileInformationByHandle(case_handle, FileCaseSensitiveInfo,
            &case_info, sizeof(case_info)) != FALSE;
        CloseHandle(case_handle);
    }
    if (case_supported) {
        HANDLE upper = CreateFileW((case_dir + L"\\Name.bin").c_str(), GENERIC_WRITE, 0,
            nullptr, CREATE_ALWAYS, 0, nullptr);
        HANDLE lower = CreateFileW((case_dir + L"\\name.bin").c_str(), GENERIC_WRITE, 0,
            nullptr, CREATE_ALWAYS, 0, nullptr);
        if (upper == INVALID_HANDLE_VALUE || lower == INVALID_HANDLE_VALUE) return 1;
        CloseHandle(upper); CloseHandle(lower);
        OwnedScan case_scan;
        if (!Check(Scan(root, 0, case_scan), L"case-sensitive scan completes") ||
            !Check(HasName(case_scan.result, L"Name.bin") && HasName(case_scan.result, L"name.bin"),
                   L"case-distinct siblings remain separate")) return 1;
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

    std::wstring hard_link = root + L"\\data\\payload-link.bin";
    if (CreateHardLinkW(hard_link.c_str(), file.c_str(), nullptr)) {
        OwnedScan links;
        if (!Check(Scan(root, 0, links), L"hard-link scan completes")) return 1;
        uint32_t first = FindNameIndex(links.result, L"payload.bin");
        uint32_t second = FindNameIndex(links.result, L"payload-link.bin");
        SmonNodeMetadata first_meta{ sizeof(SmonNodeMetadata) };
        SmonNodeMetadata second_meta{ sizeof(SmonNodeMetadata) };
        uint32_t data_index = FindNameIndex(links.result, L"data");
        SmonNodeMetadata data_meta{ sizeof(SmonNodeMetadata) };
        if (!Check(first != UINT32_MAX && second != UINT32_MAX, L"both hard-link entries remain visible") ||
            !Check(Smon_GetNodeMetadata(links.handle, first, &first_meta) &&
                   Smon_GetNodeMetadata(links.handle, second, &second_meta), L"hard-link metadata queried") ||
            !Check(first_meta.file_id == second_meta.file_id &&
                   first_meta.volume_serial == second_meta.volume_serial, L"aliases share volume-scoped identity") ||
            !Check(first_meta.link_count >= 2 && second_meta.link_count >= 2, L"link count is exposed") ||
            !Check(((first_meta.flags & SMON_NODE_META_UNIQUE_ALLOCATION) != 0) !=
                   ((second_meta.flags & SMON_NODE_META_UNIQUE_ALLOCATION) != 0),
                   L"physical allocation is accounted exactly once") ||
            !Check(data_index != UINT32_MAX &&
                   Smon_GetNodeMetadata(links.handle, data_index, &data_meta),
                   L"nested directory metadata queried") ||
            !Check(data_meta.allocated_bytes == links.result.nodes[data_index].size &&
                   data_meta.uniquely_accounted_bytes == data_meta.allocated_bytes,
                   L"directory metadata matches unique rolled-up aggregate")) return 1;
        if (ads_supported) {
            OwnedScan linked_streams;
            if (!Check(Scan(root, SMON_OPTION_INCLUDE_STREAMS, linked_streams),
                       L"hard-link ADS scan completes")) return 1;
            uint32_t stream_count = 0;
            uint32_t unique_streams = 0;
            uint64_t unique_stream_bytes = 0;
            for (uint32_t i = 0; i < linked_streams.result.node_count; ++i) {
                if ((linked_streams.result.nodes[i].flags & SMON_FLAG_STREAM) == 0) continue;
                SmonNodeMetadata metadata{ sizeof(SmonNodeMetadata) };
                if (!Smon_GetNodeMetadata(linked_streams.handle, i, &metadata)) return 1;
                ++stream_count;
                if ((metadata.flags & SMON_NODE_META_UNIQUE_ALLOCATION) != 0) ++unique_streams;
                unique_stream_bytes += metadata.uniquely_accounted_bytes;
            }
            if (!Check(stream_count == 2, L"ADS remains visible through both hard-link aliases") ||
                !Check(unique_streams == 1 && unique_stream_bytes == sizeof(stream_payload),
                       L"hard-link ADS allocation is accounted exactly once")) return 1;
        }
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
            !Check(followed.result.node_count < 64,
                   L"reparse cycle does not recurse indefinitely")) return 1;
        uint32_t cycle = FindNameIndex(followed.result, L"cycle");
        SmonNodeMetadata cycle_meta{ sizeof(SmonNodeMetadata) };
        if (!Check(cycle != UINT32_MAX && Smon_GetNodeMetadata(followed.handle, cycle, &cycle_meta),
                   L"cycle edge metadata queried") ||
            !Check((cycle_meta.flags & SMON_NODE_META_CYCLE_EDGE) != 0,
                   L"skipped cycle edge is diagnosed")) return 1;
    }

    std::filesystem::remove_all(L"\\\\?\\" + root);
    return 0;
}
