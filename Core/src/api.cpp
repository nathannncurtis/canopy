#include "../include/smon_api.h"
#include "scan_context.h"
#include "scanner_router.h"
#include "size_rollup.h"

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

BOOL WINAPI Smon_Wait(ScanHandle h, DWORD timeout_ms)
{
    if (!h) return FALSE;
    auto* ctx = static_cast<ScanContext*>(h);
    if (!ctx->thread) return FALSE;
    return WaitForSingleObject(ctx->thread, timeout_ms) == WAIT_OBJECT_0;
}

BOOL WINAPI Smon_GetResult(ScanHandle h, ScanResult* out)
{
    if (!h || !out) return FALSE;
    auto* ctx = static_cast<ScanContext*>(h);
    ctx->pool.Finalize(&ctx->result);
    RollupSizes(&ctx->result);
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
