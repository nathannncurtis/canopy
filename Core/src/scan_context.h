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
    DWORD              error       = 0;    // GetLastError() on failure, 0 = success
};
