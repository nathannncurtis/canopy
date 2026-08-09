#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#  define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#  define NOMINMAX
#endif
#include <windows.h>
#include <stdint.h>

#ifdef SMON_CORE_EXPORTS
#  define SMON_API __declspec(dllexport)
#else
#  define SMON_API __declspec(dllimport)
#endif

#define SMON_FLAG_DIRECTORY 0x01u
#define SMON_FLAG_SYMLINK   0x02u
#define SMON_FLAG_REPARSE   0x04u

#define SMON_SCANNER_UNKNOWN   0u
#define SMON_SCANNER_MFT       1u
#define SMON_SCANNER_DIRECTORY 2u

#define SMON_ABI_VERSION 1u

#define SMON_CAP_MFT_SCANNER       0x00000001ull
#define SMON_CAP_DIRECTORY_SCANNER 0x00000002ull
#define SMON_CAP_PAUSE_RESUME      0x00000004ull
#define SMON_CAP_AVX2_ASM          0x00000008ull
#define SMON_CAP_SCAN_OPTIONS      0x00000010ull
#define SMON_CAP_ERROR_INFO        0x00000020ull
#define SMON_CAP_SCAN_TELEMETRY    0x00000040ull

#define SMON_SCAN_PHASE_DISCOVERY    1u
#define SMON_SCAN_PHASE_METADATA     2u
#define SMON_SCAN_PHASE_AGGREGATION  3u
#define SMON_SCAN_PHASE_FINALIZATION 4u
#define SMON_SCAN_PHASE_COMPLETE     5u

#define SMON_ERROR_CATEGORY_NONE       0u
#define SMON_ERROR_CATEGORY_ARGUMENT   1u
#define SMON_ERROR_CATEGORY_ACCESS     2u
#define SMON_ERROR_CATEGORY_IO         3u
#define SMON_ERROR_CATEGORY_CANCELLED  4u
#define SMON_ERROR_CATEGORY_RESOURCE   5u
#define SMON_ERROR_CATEGORY_INTERNAL   6u

#define SMON_ERROR_STAGE_NONE       0u
#define SMON_ERROR_STAGE_OPEN       1u
#define SMON_ERROR_STAGE_ENUMERATE  2u
#define SMON_ERROR_STAGE_BUILD      3u
#define SMON_ERROR_PATH_CHARS       1024u

#define SMON_OPTION_EXCLUDE_HIDDEN       0x00000001u
#define SMON_OPTION_EXCLUDE_SYSTEM       0x00000002u
#define SMON_OPTION_EXCLUDE_TEMPORARY    0x00000004u
#define SMON_OPTION_EXCLUDE_REPARSE      0x00000008u
#define SMON_OPTION_FORCE_DIRECTORY_SCAN 0x00000010u

typedef struct SmonScanOptions {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t max_depth;       // 0 = unlimited; scan root is depth 0
    uint32_t worker_threads;  // 0 = automatic
    uint64_t minimum_file_size;
    uint64_t maximum_file_size; // 0 = unlimited
    const wchar_t* excluded_patterns;   // semicolon/comma/newline separated
    const wchar_t* excluded_extensions; // semicolon/comma/newline separated
} SmonScanOptions;

typedef struct SmonCapabilities {
    uint32_t struct_size; // in: caller buffer size; out: current DLL struct size
    uint32_t abi_version;
    uint64_t flags;
    uint32_t max_nodes;
    uint32_t max_name_bytes;
} SmonCapabilities;

typedef struct SmonErrorInfo {
    uint32_t struct_size;
    uint32_t win32_error;
    uint32_t category;
    uint32_t stage;
    uint32_t access_error_count;
    uint32_t access_win32_error;
    uint32_t path_length;
    wchar_t path[SMON_ERROR_PATH_CHARS];
} SmonErrorInfo;

// Additive, versioned telemetry contract. Counters are monotonic for a handle.
typedef struct SmonScanStatus {
    uint32_t struct_size;
    uint32_t phase;
    uint32_t terminal;
    uint32_t reserved;
    uint64_t dirs_visited;
    uint64_t files_visited;
    uint64_t bytes_seen;
    uint64_t skipped_directories;
    uint64_t skipped_files;
    uint64_t permission_skips;
    uint64_t error_skips;
    uint64_t changed_items;
} SmonScanStatus;

// 32 bytes, naturally aligned -- matches C# [StructLayout(LayoutKind.Sequential, Pack=8)]
typedef struct ScanNode {
    uint64_t size;         // on-disk bytes; dirs include all descendants
    uint32_t parent;       // UINT32_MAX = root
    uint32_t first_child;  // UINT32_MAX = none
    uint32_t next_sibling; // UINT32_MAX = none
    uint32_t flags;
    uint32_t name_offset;  // byte offset into name_buf
    uint32_t name_len;     // wchar_t count, not bytes
} ScanNode;

typedef struct ScanResult {
    ScanNode* nodes;
    uint32_t  node_count;
    wchar_t*  name_buf;
    uint64_t  total_bytes;
    uint64_t  file_count;
    uint64_t  dir_count;
    double    elapsed_sec;
} ScanResult;

typedef void (CALLBACK *SmonProgressCallback)(
    uint64_t dirs_visited,
    uint64_t files_visited,
    uint64_t bytes_seen,
    void*    user_data);

typedef void* ScanHandle;

#ifdef __cplusplus
extern "C" {
#endif

SMON_API ScanHandle WINAPI Smon_BeginScan(
    const wchar_t*       path,
    SmonProgressCallback callback,
    void*                user_data);
SMON_API ScanHandle WINAPI Smon_BeginScanEx(
    const wchar_t*       path,
    const SmonScanOptions* options,
    SmonProgressCallback callback,
    void*                user_data);

SMON_API DWORD WINAPI Smon_GetAbiVersion(void);
SMON_API BOOL WINAPI Smon_GetCapabilities(SmonCapabilities* capabilities);
SMON_API BOOL WINAPI Smon_Cancel(ScanHandle handle);
SMON_API BOOL WINAPI Smon_SetPaused(ScanHandle handle, BOOL paused);
SMON_API BOOL WINAPI Smon_Wait(ScanHandle handle, DWORD timeout_ms);
SMON_API DWORD WINAPI Smon_GetError(ScanHandle handle);
SMON_API BOOL WINAPI Smon_GetErrorInfo(ScanHandle handle, SmonErrorInfo* error_info);
SMON_API BOOL WINAPI Smon_GetScanStatus(ScanHandle handle, SmonScanStatus* status);
SMON_API DWORD WINAPI Smon_GetScannerKind(ScanHandle handle);
SMON_API BOOL WINAPI Smon_GetResult(ScanHandle handle, ScanResult* out);
SMON_API void WINAPI Smon_FreeResult(ScanHandle handle);
SMON_API BOOL WINAPI Smon_IsNtfsVolume(const wchar_t* path);

#ifdef __cplusplus
}
#endif
