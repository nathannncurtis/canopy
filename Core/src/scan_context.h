#pragma once
#include "../include/smon_api.h"
#include "node_pool.h"
#include "scan_options.h"
#include <atomic>
#include <windows.h>
#include <mutex>
#include <string>

struct ScanContext {
    NodePool           pool;
    ScanOptions        options;
    ScanResult         result{};
    SmonProgressCallback callback  = nullptr;
    void*              user_data   = nullptr;
    HANDLE             thread      = nullptr;
    std::atomic<bool>  cancelled   = false;
    std::atomic<bool>  paused      = false;
    HANDLE             cancel_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE             resume_event = CreateEventW(nullptr, TRUE, TRUE, nullptr);
    std::atomic<DWORD> error        = 0;    // GetLastError() on failure, 0 = success
    std::mutex         error_mutex;
    DWORD              error_category = SMON_ERROR_CATEGORY_NONE;
    DWORD              error_stage = SMON_ERROR_STAGE_NONE;
    DWORD              access_error_count = 0;
    DWORD              access_win32_error = ERROR_SUCCESS;
    std::wstring       error_path;
    DWORD              scanner_kind = 0;   // SMON_SCANNER_* selected by the router
    DWORD              filesystem_kind = SMON_FILESYSTEM_UNKNOWN;
    DWORD              fallback_reason = SMON_ROUTE_DIRECT;
    bool               cloud_backed = false;
    bool               rolled_up   = false; // guard: RollupSizes must run exactly once
    std::wstring       scan_root;
    std::wstring       display_root;
    bool               network_scan = false;
    std::atomic<DWORD> phase = SMON_SCAN_PHASE_DISCOVERY;
    std::atomic<uint64_t> dirs_visited = 0;
    std::atomic<uint64_t> files_visited = 0;
    std::atomic<uint64_t> bytes_seen = 0;
    std::atomic<uint64_t> skipped_directories = 0;
    std::atomic<uint64_t> skipped_files = 0;
    std::atomic<uint64_t> permission_skips = 0;
    std::atomic<uint64_t> error_skips = 0;
    std::atomic<uint64_t> changed_items = 0;
    FILETIME           root_write_time{};
    uint64_t           root_file_id = 0;
    bool               root_snapshot_valid = false;
    std::atomic<bool>  mutation_checked = false;

    ~ScanContext() {
        if (cancel_event) CloseHandle(cancel_event);
        if (resume_event) CloseHandle(resume_event);
    }
};

inline void RecordScanError(ScanContext* ctx, DWORD code, DWORD category,
                            DWORD stage, const std::wstring& path)
{
    std::lock_guard<std::mutex> lock(ctx->error_mutex);
    ctx->error.store(code, std::memory_order_release);
    ctx->error_category = category;
    ctx->error_stage = stage;
    ctx->error_path = path;
}

inline void RecordAccessError(ScanContext* ctx, DWORD code, const std::wstring& path)
{
    std::lock_guard<std::mutex> lock(ctx->error_mutex);
    ++ctx->access_error_count;
    ctx->skipped_directories.fetch_add(1, std::memory_order_relaxed);
    if (code == ERROR_ACCESS_DENIED || code == ERROR_SHARING_VIOLATION || code == ERROR_PRIVILEGE_NOT_HELD)
        ctx->permission_skips.fetch_add(1, std::memory_order_relaxed);
    else
        ctx->error_skips.fetch_add(1, std::memory_order_relaxed);
    ctx->access_win32_error = code;
    // Access failures are non-fatal during directory traversal, but retain the last path.
    if (ctx->error.load(std::memory_order_acquire) == ERROR_SUCCESS) {
        ctx->error_category = SMON_ERROR_CATEGORY_ACCESS;
        ctx->error_stage = SMON_ERROR_STAGE_OPEN;
        ctx->error_path = path;
    }
}

inline void BeginMutationTracking(ScanContext* ctx)
{
    HANDLE handle = CreateFileW(ctx->scan_root.c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return;
    BY_HANDLE_FILE_INFORMATION info{};
    if (GetFileInformationByHandle(handle, &info)) {
        ctx->root_write_time = info.ftLastWriteTime;
        ctx->root_file_id = (static_cast<uint64_t>(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
        ctx->root_snapshot_valid = true;
    }
    CloseHandle(handle);
}

inline void FinishMutationTracking(ScanContext* ctx)
{
    if (!ctx->root_snapshot_valid || ctx->mutation_checked.exchange(true, std::memory_order_acq_rel)) return;
    HANDLE handle = CreateFileW(ctx->scan_root.c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        ctx->changed_items.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(handle, &info)) {
        ctx->changed_items.fetch_add(1, std::memory_order_relaxed);
    } else {
        const uint64_t id = (static_cast<uint64_t>(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
        if (id != ctx->root_file_id || CompareFileTime(&info.ftLastWriteTime, &ctx->root_write_time) != 0)
            ctx->changed_items.fetch_add(1, std::memory_order_relaxed);
    }
    CloseHandle(handle);
}

inline bool WaitWhilePaused(ScanContext* ctx)
{
    if (ctx->paused.load(std::memory_order_acquire) && ctx->resume_event && ctx->cancel_event) {
        HANDLE events[] = { ctx->resume_event, ctx->cancel_event };
        if (WaitForMultipleObjects(2, events, FALSE, INFINITE) != WAIT_OBJECT_0)
            return false;
    }
    return !ctx->cancelled.load(std::memory_order_acquire);
}
