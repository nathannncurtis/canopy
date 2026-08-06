#pragma once
#include "../include/smon_api.h"
#include "node_pool.h"
#include "scan_options.h"
#include <atomic>
#include <windows.h>

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
    DWORD              scanner_kind = 0;   // SMON_SCANNER_* selected by the router
    bool               rolled_up   = false; // guard: RollupSizes must run exactly once

    ~ScanContext() {
        if (cancel_event) CloseHandle(cancel_event);
        if (resume_event) CloseHandle(resume_event);
    }
};

inline bool WaitWhilePaused(ScanContext* ctx)
{
    if (ctx->paused.load(std::memory_order_acquire) && ctx->resume_event && ctx->cancel_event) {
        HANDLE events[] = { ctx->resume_event, ctx->cancel_event };
        if (WaitForMultipleObjects(2, events, FALSE, INFINITE) != WAIT_OBJECT_0)
            return false;
    }
    return !ctx->cancelled.load(std::memory_order_acquire);
}
