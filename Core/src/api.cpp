#include "../include/smon_api.h"
#include "scan_context.h"
#include "scanner_router.h"
#include "size_rollup.h"
#include "cpu_features.h"
#include <cstring>

DWORD WINAPI Smon_GetAbiVersion(void)
{
    return SMON_ABI_VERSION;
}

BOOL WINAPI Smon_GetCapabilities(SmonCapabilities* capabilities)
{
    if (!capabilities || capabilities->struct_size < sizeof(SmonCapabilities)) {
        SetLastError(capabilities ? ERROR_INSUFFICIENT_BUFFER : ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    SmonCapabilities value{};
    value.struct_size = sizeof(value);
    value.abi_version = SMON_ABI_VERSION;
    value.flags = SMON_CAP_MFT_SCANNER |
                  SMON_CAP_DIRECTORY_SCANNER |
                  SMON_CAP_PAUSE_RESUME;
    if (CpuHasAvx2()) value.flags |= SMON_CAP_AVX2_ASM;
    value.max_nodes = NodePool::MaxNodes;
    value.max_name_bytes = NodePool::MaxNameBytes;
    std::memcpy(capabilities, &value, sizeof(value));
    SetLastError(ERROR_SUCCESS);
    return TRUE;
}

ScanHandle WINAPI Smon_BeginScan(const wchar_t* path,
                                 SmonProgressCallback cb,
                                 void* ud)
{
    auto* ctx       = new ScanContext();
    ctx->callback   = cb;
    ctx->user_data  = ud;
    if (!RouterBeginScan(ctx, path)) {
        delete ctx;
        return nullptr;
    }
    return ctx;
}

BOOL WINAPI Smon_Cancel(ScanHandle h)
{
    if (!h) return FALSE;
    static_cast<ScanContext*>(h)->cancelled = true;
    return TRUE;
}

BOOL WINAPI Smon_SetPaused(ScanHandle h, BOOL paused)
{
    if (!h) return FALSE;
    static_cast<ScanContext*>(h)->paused.store(paused != FALSE,
                                               std::memory_order_release);
    return TRUE;
}

BOOL WINAPI Smon_Wait(ScanHandle h, DWORD timeout_ms)
{
    if (!h) return FALSE;
    auto* ctx = static_cast<ScanContext*>(h);
    if (!ctx->thread) return FALSE;
    return WaitForSingleObject(ctx->thread, timeout_ms) == WAIT_OBJECT_0;
}

DWORD WINAPI Smon_GetError(ScanHandle h)
{
    return h ? static_cast<ScanContext*>(h)->error : ERROR_INVALID_HANDLE;
}

DWORD WINAPI Smon_GetScannerKind(ScanHandle h)
{
    return h ? static_cast<ScanContext*>(h)->scanner_kind : SMON_SCANNER_UNKNOWN;
}

BOOL WINAPI Smon_GetResult(ScanHandle h, ScanResult* out)
{
    if (!h || !out) return FALSE;
    auto* ctx = static_cast<ScanContext*>(h);
    ctx->pool.Finalize(&ctx->result);
    if (!ctx->rolled_up) {
        RollupSizes(&ctx->result);
        ctx->rolled_up = true;
    }
    *out = ctx->result;
    return ctx->error == 0;
}

void WINAPI Smon_FreeResult(ScanHandle h)
{
    if (!h) return;
    auto* ctx = static_cast<ScanContext*>(h);
    if (ctx->thread)
        CloseHandle(ctx->thread);
    delete ctx;
}

BOOL WINAPI Smon_IsNtfsVolume(const wchar_t* path)
{
    return RouterIsNtfs(path) ? TRUE : FALSE;
}
