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
    bool               rolled_up   = false; // guard: RollupSizes must run exactly once

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
    ctx->access_win32_error = code;
    // Access failures are non-fatal during directory traversal, but retain the last path.
    if (ctx->error.load(std::memory_order_acquire) == ERROR_SUCCESS) {
        ctx->error_category = SMON_ERROR_CATEGORY_ACCESS;
        ctx->error_stage = SMON_ERROR_STAGE_OPEN;
        ctx->error_path = path;
    }
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
