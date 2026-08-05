#pragma once
#include "../include/smon_api.h"
#include "node_pool.h"
#include <atomic>
#include <windows.h>

struct ScanContext {
    NodePool           pool;
    ScanResult         result{};
    SmonProgressCallback callback  = nullptr;
    void*              user_data   = nullptr;
    HANDLE             thread      = nullptr;
    std::atomic<bool>  cancelled   = false;
    std::atomic<bool>  paused      = false;
    DWORD              error       = 0;    // GetLastError() on failure, 0 = success
    DWORD              scanner_kind = 0;   // SMON_SCANNER_* selected by the router
    bool               rolled_up   = false; // guard: RollupSizes must run exactly once
};

inline bool WaitWhilePaused(ScanContext* ctx)
{
    while (ctx->paused.load(std::memory_order_acquire)) {
        if (ctx->cancelled.load(std::memory_order_acquire))
            return false;
        Sleep(25);
    }
    return !ctx->cancelled.load(std::memory_order_acquire);
}
