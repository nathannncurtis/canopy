#include "../include/smon_api.h"

#include <windows.h>
#include <winioctl.h>

#include <cstdio>
#include <filesystem>
#include <string>
#include <unordered_map>

namespace {

struct HandleCloser {
    void operator()(HANDLE handle) const
    {
        if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
    }
};

class FixtureRoot {
public:
    FixtureRoot()
    {
        wchar_t temp[MAX_PATH]{};
        wchar_t name[MAX_PATH]{};
        if (GetTempPathW(MAX_PATH, temp) == 0 ||
            GetTempFileNameW(temp, L"cny", 0, name) == 0)
            return;
        DeleteFileW(name);
        path_ = L"\\\\?\\" + std::wstring(name);
        if (!CreateDirectoryW(path_.c_str(), nullptr)) path_.clear();
    }

    ~FixtureRoot()
    {
        std::error_code error;
        if (!path_.empty()) std::filesystem::remove_all(path_, error);
    }

    const std::wstring& path() const { return path_; }
    bool valid() const { return !path_.empty(); }

private:
    std::wstring path_;
};

bool Check(bool condition, const wchar_t* message)
{
    if (condition) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

void Skip(const wchar_t* fixture, DWORD error)
{
    wprintf(L"SKIP: %s (error %lu)\n", fixture, error);
}

bool MakeDirectory(const std::wstring& path)
{
    return CreateDirectoryW(path.c_str(), nullptr) != FALSE;
}

bool WriteSizedFile(const std::wstring& path, DWORD size)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    std::string bytes(size, 'x');
    DWORD written = 0;
    bool ok = WriteFile(file, bytes.data(), size, &written, nullptr) != FALSE &&
              written == size;
    CloseHandle(file);
    return ok;
}

uint64_t AllocationSize(const std::wstring& path)
{
    HANDLE file = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return UINT64_MAX;
    FILE_STANDARD_INFO info{};
    bool ok = GetFileInformationByHandleEx(file, FileStandardInfo, &info, sizeof(info)) != FALSE;
    CloseHandle(file);
    return ok ? static_cast<uint64_t>(info.AllocationSize.QuadPart) : UINT64_MAX;
}

bool MakeSparseFile(const std::wstring& path)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD returned = 0;
    bool ok = DeviceIoControl(file, FSCTL_SET_SPARSE, nullptr, 0, nullptr, 0,
                              &returned, nullptr) != FALSE;
    LARGE_INTEGER offset{};
    offset.QuadPart = 8ll * 1024 * 1024;
    char byte = 's';
    DWORD written = 0;
    ok = ok && SetFilePointerEx(file, offset, nullptr, FILE_BEGIN) != FALSE &&
         WriteFile(file, &byte, 1, &written, nullptr) != FALSE && written == 1;
    CloseHandle(file);
    return ok;
}

bool MakeCompressedFile(const std::wstring& path)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    USHORT format = COMPRESSION_FORMAT_DEFAULT;
    DWORD returned = 0;
    bool ok = DeviceIoControl(file, FSCTL_SET_COMPRESSION, &format, sizeof(format),
                              nullptr, 0, &returned, nullptr) != FALSE;
    std::string bytes(128 * 1024, 'c');
    DWORD written = 0;
    ok = ok && WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()),
                         &written, nullptr) != FALSE && written == bytes.size();
    CloseHandle(file);
    return ok;
}

std::wstring NodeName(const ScanResult& result, const ScanNode& node)
{
    const auto* bytes = reinterpret_cast<const BYTE*>(result.name_buf);
    const auto* name = reinterpret_cast<const wchar_t*>(bytes + node.name_offset);
    return std::wstring(name, node.name_len);
}

} // namespace

int wmain()
{
    FixtureRoot fixture;
    if (!Check(fixture.valid(), L"temporary corpus root created")) return 1;

    const std::wstring& root = fixture.path();
    const std::wstring ordinary = root + L"\\ordinary.bin";
    const std::wstring unicode = root + L"\\unicode-\u03A9-\u4E2D-[] #.txt";
    if (!Check(WriteSizedFile(ordinary, 37), L"ordinary fixture created") ||
        !Check(WriteSizedFile(unicode, 73), L"edge-name fixture created"))
        return 1;

    std::wstring deep = root;
    for (int i = 0; i < 20; ++i) {
        deep += L"\\depth-segment-0123456789";
        if (!Check(MakeDirectory(deep), L"deep directory fixture created")) return 1;
    }
    const std::wstring deep_file = deep + L"\\deep-leaf.dat";
    if (!Check(deep_file.size() > MAX_PATH, L"deep fixture exceeds MAX_PATH") ||
        !Check(WriteSizedFile(deep_file, 91), L"deep file fixture created"))
        return 1;

    bool hard_link = CreateHardLinkW((root + L"\\ordinary-hardlink.bin").c_str(),
                                     ordinary.c_str(), nullptr) != FALSE;
    if (!hard_link) Skip(L"hard link", GetLastError());

    const std::wstring sparse = root + L"\\sparse.bin";
    bool sparse_file = MakeSparseFile(sparse);
    if (!sparse_file) {
        DWORD error = GetLastError();
        DeleteFileW(sparse.c_str());
        Skip(L"sparse file", error);
    }

    const std::wstring compressed = root + L"\\compressed.bin";
    bool compressed_file = MakeCompressedFile(compressed);
    if (!compressed_file) {
        DWORD error = GetLastError();
        DeleteFileW(compressed.c_str());
        Skip(L"compressed file", error);
    }

    bool reparse = CreateSymbolicLinkW((root + L"\\racy-dangling-link").c_str(),
                                       (root + L"\\target-that-disappeared").c_str(), 0) != FALSE;
    if (!reparse) Skip(L"dangling reparse point", GetLastError());

    const std::wstring locked_dir = root + L"\\inaccessible-while-scanning";
    if (!Check(MakeDirectory(locked_dir), L"locked directory fixture created") ||
        !Check(WriteSizedFile(locked_dir + L"\\must-not-be-seen.txt", 11),
               L"locked child fixture created"))
        return 1;
    HANDLE locked = CreateFileW(locked_dir.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING,
                                FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (!Check(locked != INVALID_HANDLE_VALUE, L"exclusive directory handle acquired")) return 1;

    SmonScanOptions options{};
    options.struct_size = sizeof(options);
    options.flags = SMON_OPTION_FORCE_DIRECTORY_SCAN;
    options.worker_threads = 1;
    ScanHandle handle = Smon_BeginScanEx(root.c_str(), &options, nullptr, nullptr);
    if (!Check(handle != nullptr, L"corpus scan started") ||
        !Check(Smon_Wait(handle, INFINITE) != FALSE, L"corpus scan completed")) {
        CloseHandle(locked);
        if (handle) Smon_FreeResult(handle);
        return 1;
    }

    // Mutate after collection but before materialization. The aggregate snapshot
    // must be marked stale even if the new item was not part of enumeration.
    const std::wstring mutation = root + L"\\created-during-scan.txt";
    if (!Check(WriteSizedFile(mutation, 13), L"mutation fixture created")) {
        CloseHandle(locked);
        Smon_FreeResult(handle);
        return 1;
    }

    ScanResult result{};
    if (!Check(Smon_GetResult(handle, &result) != FALSE, L"corpus result returned")) {
        CloseHandle(locked);
        Smon_FreeResult(handle);
        return 1;
    }
    SmonScanStatus status{};
    status.struct_size = sizeof(status);
    if (!Check(Smon_GetScanStatus(handle, &status) != FALSE, L"corpus telemetry returned") ||
        !Check(status.skipped_directories >= 1, L"inaccessible directory counted") ||
        !Check(status.permission_skips >= 1, L"permission skip classified") ||
        !Check(status.changed_items >= 1, L"concurrent mutation marked snapshot stale")) {
        CloseHandle(locked);
        Smon_FreeResult(handle);
        return 1;
    }

    std::unordered_map<std::wstring, const ScanNode*> nodes;
    for (uint32_t i = 1; i < result.node_count; ++i)
        nodes.emplace(NodeName(result, result.nodes[i]), &result.nodes[i]);
    auto metadata_for = [&](const wchar_t* name, SmonNodeMetadata* metadata) {
        for (uint32_t i = 1; i < result.node_count; ++i) {
            if (NodeName(result, result.nodes[i]) == name) {
                metadata->struct_size = sizeof(*metadata);
                return Smon_GetNodeMetadata(handle, i, metadata) != FALSE;
            }
        }
        return false;
    };

    bool ok = true;
    ok &= Check(nodes.contains(L"ordinary.bin"), L"ordinary file discovered");
    ok &= Check(nodes.contains(L"unicode-\u03A9-\u4E2D-[] #.txt"), L"edge name preserved exactly");
    ok &= Check(nodes.contains(L"deep-leaf.dat"), L"deep path traversed");
    ok &= Check(nodes.contains(L"inaccessible-while-scanning"), L"locked directory discovered");
    ok &= Check(!nodes.contains(L"must-not-be-seen.txt"), L"locked directory not traversed");
    if (nodes.contains(L"ordinary.bin"))
        ok &= Check(nodes.at(L"ordinary.bin")->size == AllocationSize(ordinary),
                    L"ordinary allocation size is exact");
    if (nodes.contains(L"unicode-\u03A9-\u4E2D-[] #.txt"))
        ok &= Check(nodes.at(L"unicode-\u03A9-\u4E2D-[] #.txt")->size == AllocationSize(unicode),
                    L"edge-name allocation size is exact");
    if (nodes.contains(L"deep-leaf.dat"))
        ok &= Check(nodes.at(L"deep-leaf.dat")->size == AllocationSize(deep_file),
                    L"deep-file allocation size is exact");
    for (uint32_t i = 1; i < result.node_count; ++i)
        ok &= Check(result.nodes[i].parent < i, L"corpus parents precede descendants");
    if (hard_link) {
        ok &= Check(nodes.contains(L"ordinary-hardlink.bin"), L"hard link discovered separately");
        if (nodes.contains(L"ordinary.bin") && nodes.contains(L"ordinary-hardlink.bin"))
            ok &= Check(nodes.at(L"ordinary.bin")->size ==
                            nodes.at(L"ordinary-hardlink.bin")->size,
                        L"hard-link allocation semantics agree");
    }
    if (sparse_file) {
        uint64_t expected = AllocationSize(sparse);
        SmonNodeMetadata metadata{};
        ok &= Check(nodes.contains(L"sparse.bin"), L"sparse file discovered");
        if (nodes.contains(L"sparse.bin"))
            ok &= Check(expected != UINT64_MAX && nodes.at(L"sparse.bin")->size == expected,
                        L"sparse allocation size reported deterministically") &&
                  Check(metadata_for(L"sparse.bin", &metadata) &&
                        (metadata.flags & SMON_NODE_META_SPARSE) != 0,
                        L"sparse storage classification preserved");
    }
    if (compressed_file) {
        uint64_t expected = AllocationSize(compressed);
        SmonNodeMetadata metadata{};
        ok &= Check(nodes.contains(L"compressed.bin"), L"compressed file discovered");
        if (nodes.contains(L"compressed.bin"))
            ok &= Check(expected != UINT64_MAX && nodes.at(L"compressed.bin")->size == expected,
                        L"compressed allocation size reported deterministically") &&
                  Check(metadata_for(L"compressed.bin", &metadata) &&
                        (metadata.flags & SMON_NODE_META_COMPRESSED) != 0,
                        L"compressed storage classification preserved");
    }
    if (reparse) {
        ok &= Check(nodes.contains(L"racy-dangling-link"), L"dangling reparse point discovered");
        if (nodes.contains(L"racy-dangling-link"))
            ok &= Check((nodes.at(L"racy-dangling-link")->flags & SMON_FLAG_REPARSE) != 0,
                        L"dangling reparse point flagged");
    }

    Smon_FreeResult(handle);
    CloseHandle(locked);

    ScanHandle stable_handle = Smon_BeginScanEx(deep.c_str(), &options, nullptr, nullptr);
    if (!Check(stable_handle != nullptr, L"stable scan started") ||
        !Check(Smon_Wait(stable_handle, INFINITE) != FALSE, L"stable scan completed")) {
        if (stable_handle) Smon_FreeResult(stable_handle);
        return 1;
    }
    ScanResult stable_result{};
    SmonScanStatus stable_status{};
    stable_status.struct_size = sizeof(stable_status);
    ok &= Check(Smon_GetResult(stable_handle, &stable_result) != FALSE,
                L"stable result returned");
    ok &= Check(Smon_GetScanStatus(stable_handle, &stable_status) != FALSE,
                L"stable telemetry returned");
    ok &= Check(stable_status.changed_items == 0,
                L"stable fixture is not marked changed");
    Smon_FreeResult(stable_handle);
    return ok ? 0 : 1;
}
