#include "scanner_router.h"
#include "scan_context.h"
#include "mft_scanner.h"
#include "dir_scanner.h"
#include <cstring>

bool RouterIsNtfs(const wchar_t* path)
{
    if (!path || !path[0])
        return false;

    // Build volume root: e.g. "C:\" from "C:\Windows\..."
    wchar_t root[MAX_PATH];
    if (!GetVolumePathNameW(path, root, MAX_PATH))
        return false;

    wchar_t fs_name[64] = {};
    if (!GetVolumeInformationW(root, nullptr, 0, nullptr, nullptr, nullptr,
                               fs_name, static_cast<DWORD>(sizeof(fs_name) / sizeof(wchar_t))))
        return false;

    return wcscmp(fs_name, L"NTFS") == 0;
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

    LPTHREAD_START_ROUTINE thread_proc = nullptr;

    // UNC paths go straight to dir_scanner; no MFT access possible.
    if (path[0] == L'\\' && path[1] == L'\\') {
        thread_proc = DirScanThread;
    } else if (RouterIsNtfs(path) && RouterIsElevated()) {
        thread_proc = MftScanThread;
    } else {
        thread_proc = DirScanThread;
    }

    // Store path in result so thread procs can read it.
    ctx->result.nodes    = nullptr;
    ctx->result.name_buf = nullptr;

    // Pass a heap copy of the path so the caller's string stays valid.
    size_t len = wcslen(path) + 1;
    wchar_t* path_copy = new wchar_t[len];
    memcpy(path_copy, path, len * sizeof(wchar_t));
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
