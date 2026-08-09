#include "scanner_router.h"
#include "scan_context.h"
#include "mft_scanner.h"
#include "dir_scanner.h"
#include "filesystem_policy.h"
#include "network_scan_policy.h"
#include <winnetwk.h>
#include <cstring>

bool RouterIsNtfs(const wchar_t* path)
{
    if (!path || !path[0])
        return false;

    std::wstring normalized = NormalizeExtendedPath(path);
    if (normalized.empty()) return false;
    // Build volume root: e.g. "C:\" from "C:\Windows\..."
    wchar_t root[MAX_PATH];
    if (!GetVolumePathNameW(normalized.c_str(), root, MAX_PATH))
        return false;

    wchar_t fs_name[64] = {};
    if (!GetVolumeInformationW(root, nullptr, 0, nullptr, nullptr, nullptr,
                               fs_name, static_cast<DWORD>(sizeof(fs_name) / sizeof(wchar_t))))
        return false;

    return ClassifyFilesystem(fs_name) == FilesystemKind::Ntfs;
}

bool RouterIsElevated()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        return false;

    TOKEN_ELEVATION elev{};
    DWORD returned = 0;
    BOOL ok = GetTokenInformation(token, TokenElevation, &elev, sizeof(elev), &returned);
    CloseHandle(token);

    return ok && elev.TokenIsElevated != 0;
}

bool RouterBeginScan(ScanContext* ctx, const wchar_t* path)
{
    if (!ctx || !path)
        return false;

    ctx->display_root = path;
    std::wstring resolved(path);
    if (resolved.size() >= 2 && resolved[1] == L':') {
        wchar_t drive[] = { resolved[0], L':', L'\0' };
        DWORD length = 0;
        if (WNetGetConnectionW(drive, nullptr, &length) == ERROR_MORE_DATA && length > 1) {
            std::wstring remote(length, L'\0');
            if (WNetGetConnectionW(drive, remote.data(), &length) == NO_ERROR) {
                remote.resize(wcslen(remote.c_str()));
                resolved = CombineMappedRemotePath(resolved, remote);
            }
        }
    }
    std::wstring normalized = NormalizeExtendedPath(resolved);
    if (normalized.empty()) { SetLastError(ERROR_INVALID_NAME); return false; }
    ctx->scan_root = normalized;
    BeginMutationTracking(ctx);
    LPTHREAD_START_ROUTINE thread_proc = nullptr;

    bool network = normalized.starts_with(L"\\\\?\\UNC\\");
    ctx->network_scan = network;
    DWORD attributes = GetFileAttributesW(normalized.c_str());
    ctx->cloud_backed = attributes != INVALID_FILE_ATTRIBUTES && IsCloudPlaceholderAttributes(attributes);
    wchar_t volume_root[MAX_PATH]{};
    wchar_t fs_name[64]{};
    if (network) ctx->filesystem_kind = SMON_FILESYSTEM_NETWORK;
    else if (GetVolumePathNameW(normalized.c_str(), volume_root, MAX_PATH) &&
             GetVolumeInformationW(volume_root, nullptr, 0, nullptr, nullptr, nullptr, fs_name, 64))
        ctx->filesystem_kind = static_cast<DWORD>(ClassifyFilesystem(fs_name));

    if (ctx->options.force_directory_scanner) {
        thread_proc = DirScanThread;
        ctx->scanner_kind = SMON_SCANNER_DIRECTORY;
        ctx->fallback_reason = SMON_ROUTE_EXPLICIT_DIRECTORY;
    } else if (ctx->options.HasConstrainingOptions()) {
        thread_proc = DirScanThread; ctx->scanner_kind = SMON_SCANNER_DIRECTORY;
        ctx->fallback_reason = SMON_ROUTE_CONSTRAINING_OPTIONS;
    } else if (network) {
        thread_proc = DirScanThread; ctx->scanner_kind = SMON_SCANNER_DIRECTORY;
        ctx->fallback_reason = SMON_ROUTE_NETWORK_PATH;
    } else if (ctx->cloud_backed) {
        thread_proc = DirScanThread; ctx->scanner_kind = SMON_SCANNER_DIRECTORY;
        ctx->fallback_reason = SMON_ROUTE_CLOUD_PLACEHOLDER;
    } else if (ctx->filesystem_kind == SMON_FILESYSTEM_NTFS && RouterIsElevated()) {
        thread_proc = MftScanThread;
        ctx->scanner_kind = SMON_SCANNER_MFT;
    } else if (ctx->filesystem_kind == SMON_FILESYSTEM_NTFS) {
        thread_proc = DirScanThread; ctx->scanner_kind = SMON_SCANNER_DIRECTORY;
        ctx->fallback_reason = SMON_ROUTE_NOT_ELEVATED;
    } else {
        thread_proc = DirScanThread;
        ctx->scanner_kind = SMON_SCANNER_DIRECTORY;
        ctx->fallback_reason = SMON_ROUTE_UNSUPPORTED_FILESYSTEM;
    }

    // Store path in result so thread procs can read it.
    ctx->result.nodes    = nullptr;
    ctx->result.name_buf = nullptr;

    // Pass a heap copy of the path so the caller's string stays valid.
    size_t len = normalized.size() + 1;
    wchar_t* path_copy = new wchar_t[len];
    memcpy(path_copy, normalized.c_str(), len * sizeof(wchar_t));
    // Stash path pointer in name_buf temporarily; thread procs must free it.
    ctx->result.name_buf = path_copy;

    ctx->thread = CreateThread(nullptr, 0, thread_proc, ctx, 0, nullptr);
    if (!ctx->thread) {
        delete[] path_copy;
        ctx->result.name_buf = nullptr;
        return false;
    }
    return true;
}
