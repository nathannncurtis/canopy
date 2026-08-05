#include "../include/smon_api.h"
#include "scan_context.h"
#include "scanner_router.h"
#include "size_rollup.h"
#include "cpu_features.h"
#include <cstring>
#include <new>

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
                  SMON_CAP_PAUSE_RESUME |
                  SMON_CAP_SCAN_OPTIONS;
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
    return Smon_BeginScanEx(path, nullptr, cb, ud);
}

ScanHandle WINAPI Smon_BeginScanEx(const wchar_t* path,
                                   const SmonScanOptions* options,
                                   SmonProgressCallback cb,
                                   void* ud)
{
    if (!path || !path[0]) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    if (options && options->struct_size < sizeof(SmonScanOptions)) {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return nullptr;
    }

    auto* ctx = new (std::nothrow) ScanContext();
    if (!ctx) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    }
    ctx->callback   = cb;
    ctx->user_data  = ud;
    try {
        if (options) {
        constexpr uint32_t known_flags =
            SMON_OPTION_EXCLUDE_HIDDEN |
            SMON_OPTION_EXCLUDE_SYSTEM |
            SMON_OPTION_EXCLUDE_TEMPORARY |
            SMON_OPTION_EXCLUDE_REPARSE |
            SMON_OPTION_FORCE_DIRECTORY_SCAN;
        if ((options->flags & ~known_flags) != 0) {
            delete ctx;
            SetLastError(ERROR_INVALID_FLAGS);
            return nullptr;
        }
        ctx->options.max_depth = options->max_depth == 0 ? UINT32_MAX : options->max_depth;
        ctx->options.worker_threads = options->worker_threads;
        ctx->options.minimum_file_size = options->minimum_file_size;
        ctx->options.maximum_file_size = options->maximum_file_size == 0
            ? UINT64_MAX : options->maximum_file_size;
        ctx->options.include_hidden =
            (options->flags & SMON_OPTION_EXCLUDE_HIDDEN) == 0;
        ctx->options.include_system =
            (options->flags & SMON_OPTION_EXCLUDE_SYSTEM) == 0;
        ctx->options.include_temporary =
            (options->flags & SMON_OPTION_EXCLUDE_TEMPORARY) == 0;
        ctx->options.include_reparse_points =
            (options->flags & SMON_OPTION_EXCLUDE_REPARSE) == 0;
        ctx->options.force_directory_scanner =
            (options->flags & SMON_OPTION_FORCE_DIRECTORY_SCAN) != 0;
        if (options->excluded_patterns)
            ctx->options.SetExcludedPatterns(options->excluded_patterns);
        if (options->excluded_extensions)
            ctx->options.SetExcludedExtensions(options->excluded_extensions);
        DWORD option_error = ctx->options.Validate();
        if (option_error != ERROR_SUCCESS) {
            delete ctx;
            SetLastError(option_error);
            return nullptr;
        }
        }
        if (!RouterBeginScan(ctx, path)) {
            delete ctx;
            if (GetLastError() == ERROR_SUCCESS) SetLastError(ERROR_GEN_FAILURE);
            return nullptr;
        }
    } catch (const std::bad_alloc&) {
        delete ctx;
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    } catch (...) {
        delete ctx;
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    SetLastError(ERROR_SUCCESS);
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
