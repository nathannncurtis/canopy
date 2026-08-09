#include "../include/smon_api.h"
#include "scan_context.h"
#include "scanner_router.h"
#include "size_rollup.h"
#include "cpu_features.h"
#include <cstring>
#include <cstddef>
#include <new>
#include <cwchar>

static DWORD ClassifyError(DWORD code)
{
    if (code == ERROR_SUCCESS) return SMON_ERROR_CATEGORY_NONE;
    if (code == ERROR_CANCELLED) return SMON_ERROR_CATEGORY_CANCELLED;
    if (code == ERROR_ACCESS_DENIED || code == ERROR_SHARING_VIOLATION || code == ERROR_PRIVILEGE_NOT_HELD)
        return SMON_ERROR_CATEGORY_ACCESS;
    if (code == ERROR_NOT_ENOUGH_MEMORY || code == ERROR_INSUFFICIENT_BUFFER || code == ERROR_DISK_FULL)
        return SMON_ERROR_CATEGORY_RESOURCE;
    if (code == ERROR_INVALID_PARAMETER || code == ERROR_INVALID_FLAGS || code == ERROR_BAD_ARGUMENTS)
        return SMON_ERROR_CATEGORY_ARGUMENT;
    return SMON_ERROR_CATEGORY_IO;
}

DWORD WINAPI Smon_GetAbiVersion(void)
{
    return SMON_ABI_VERSION;
}

BOOL WINAPI Smon_GetCapabilities(SmonCapabilities* capabilities)
{
    if (!capabilities) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    const uint32_t caller_size = capabilities->struct_size;
    constexpr uint32_t minimum_size = static_cast<uint32_t>(offsetof(SmonCapabilities, flags) + sizeof(uint64_t));
    if (caller_size < minimum_size) {
        capabilities->struct_size = sizeof(SmonCapabilities);
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return FALSE;
    }
    SmonCapabilities value{};
    value.struct_size = sizeof(value);
    value.abi_version = SMON_ABI_VERSION;
    value.flags = SMON_CAP_MFT_SCANNER |
                  SMON_CAP_DIRECTORY_SCANNER |
                  SMON_CAP_PAUSE_RESUME |
                  SMON_CAP_SCAN_OPTIONS |
                  SMON_CAP_ERROR_INFO |
                  SMON_CAP_SCAN_TELEMETRY;
#if defined(SMON_ENABLE_AVX2_SUM)
    if (CpuHasAvx2()) value.flags |= SMON_CAP_AVX2_ASM;
#endif
#if defined(_M_ARM64) || defined(__aarch64__)
    value.flags |= SMON_CAP_ARM64_INTRINSICS;
#endif
    value.max_nodes = NodePool::MaxNodes;
    value.max_name_bytes = NodePool::MaxNameBytes;
    std::memcpy(capabilities, &value, caller_size < sizeof(value) ? caller_size : sizeof(value));
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
    if (!ctx->cancel_event || !ctx->resume_event) {
        DWORD error = GetLastError();
        delete ctx;
        SetLastError(error == ERROR_SUCCESS ? ERROR_NOT_ENOUGH_MEMORY : error);
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
    auto* ctx = static_cast<ScanContext*>(h);
    ctx->cancelled.store(true, std::memory_order_release);
    if (ctx->cancel_event) SetEvent(ctx->cancel_event);
    return TRUE;
}

BOOL WINAPI Smon_SetPaused(ScanHandle h, BOOL paused)
{
    if (!h) return FALSE;
    auto* ctx = static_cast<ScanContext*>(h);
    ctx->paused.store(paused != FALSE, std::memory_order_release);
    if (ctx->resume_event) {
        if (paused) ResetEvent(ctx->resume_event);
        else SetEvent(ctx->resume_event);
    }
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
    return h ? static_cast<ScanContext*>(h)->error.load(std::memory_order_acquire) : ERROR_INVALID_HANDLE;
}

BOOL WINAPI Smon_GetErrorInfo(ScanHandle h, SmonErrorInfo* info)
{
    if (!h || !info) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
    const uint32_t caller_size = info->struct_size;
    constexpr uint32_t minimum_size = static_cast<uint32_t>(offsetof(SmonErrorInfo, path_length) + sizeof(uint32_t));
    if (caller_size < minimum_size) {
        info->struct_size = sizeof(SmonErrorInfo);
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return FALSE;
    }
    auto* ctx = static_cast<ScanContext*>(h);
    SmonErrorInfo value{};
    value.struct_size = sizeof(value);
    value.win32_error = ctx->error.load(std::memory_order_acquire);
    {
        std::lock_guard<std::mutex> lock(ctx->error_mutex);
        value.category = ctx->error_category;
        value.stage = ctx->error_stage;
        value.access_error_count = ctx->access_error_count;
        value.access_win32_error = ctx->access_win32_error;
        value.path_length = static_cast<uint32_t>((ctx->error_path.size() < SMON_ERROR_PATH_CHARS - 1)
            ? ctx->error_path.size() : SMON_ERROR_PATH_CHARS - 1);
        if (value.path_length) std::wmemcpy(value.path, ctx->error_path.data(), value.path_length);
        value.path[value.path_length] = L'\0';
    }
    if (value.category == SMON_ERROR_CATEGORY_NONE)
        value.category = ClassifyError(value.win32_error);
    std::memcpy(info, &value, caller_size < sizeof(value) ? caller_size : sizeof(value));
    SetLastError(ERROR_SUCCESS);
    return TRUE;
}

BOOL WINAPI Smon_GetScanStatus(ScanHandle h, SmonScanStatus* status)
{
    if (!h || !status) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
    const uint32_t caller_size = status->struct_size;
    constexpr uint32_t minimum_size = static_cast<uint32_t>(offsetof(SmonScanStatus, bytes_seen) + sizeof(uint64_t));
    if (caller_size < minimum_size) {
        status->struct_size = sizeof(SmonScanStatus);
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return FALSE;
    }
    auto* ctx = static_cast<ScanContext*>(h);
    SmonScanStatus value{};
    value.struct_size = sizeof(value);
    value.phase = ctx->phase.load(std::memory_order_acquire);
    value.terminal = value.phase == SMON_SCAN_PHASE_COMPLETE ? TRUE : FALSE;
    value.dirs_visited = ctx->dirs_visited.load(std::memory_order_relaxed);
    value.files_visited = ctx->files_visited.load(std::memory_order_relaxed);
    value.bytes_seen = ctx->bytes_seen.load(std::memory_order_relaxed);
    value.skipped_directories = ctx->skipped_directories.load(std::memory_order_relaxed);
    value.skipped_files = ctx->skipped_files.load(std::memory_order_relaxed);
    value.permission_skips = ctx->permission_skips.load(std::memory_order_relaxed);
    value.error_skips = ctx->error_skips.load(std::memory_order_relaxed);
    value.changed_items = ctx->changed_items.load(std::memory_order_relaxed);
    std::memcpy(status, &value, caller_size < sizeof(value) ? caller_size : sizeof(value));
    SetLastError(ERROR_SUCCESS);
    return TRUE;
}

DWORD WINAPI Smon_GetScannerKind(ScanHandle h)
{
    return h ? static_cast<ScanContext*>(h)->scanner_kind : SMON_SCANNER_UNKNOWN;
}

BOOL WINAPI Smon_GetResult(ScanHandle h, ScanResult* out)
{
    if (!h || !out) return FALSE;
    auto* ctx = static_cast<ScanContext*>(h);
    FinishMutationTracking(ctx);
    ctx->phase.store(SMON_SCAN_PHASE_AGGREGATION, std::memory_order_release);
    ctx->pool.Finalize(&ctx->result);
    if (!ctx->rolled_up) {
        RollupSizes(&ctx->result);
        ctx->rolled_up = true;
    }
    ctx->phase.store(SMON_SCAN_PHASE_FINALIZATION, std::memory_order_release);
    *out = ctx->result;
    ctx->phase.store(SMON_SCAN_PHASE_COMPLETE, std::memory_order_release);
    return ctx->error.load(std::memory_order_acquire) == 0;
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
