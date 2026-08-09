#pragma once
#include <windows.h>
#include <stdint.h>
#include <string>
#include <string_view>

enum class NetworkFailureKind { None, Disconnected, Authentication, Timeout, NameResolution, PathMissing, Other };
enum class NetworkRetryAction { Complete, Retry, Fail, Cancelled };
enum class NetworkFailureDisposition { FatalScan, SkipDescendant };
NetworkFailureKind ClassifyNetworkError(DWORD error);
bool IsRetryableNetworkFailure(NetworkFailureKind failure);
DWORD NetworkRetryDelay(uint32_t attempt, DWORD base_delay_ms, DWORD maximum_delay_ms);
bool IsUncPath(std::wstring_view path);
std::wstring CombineMappedRemotePath(std::wstring_view input, std::wstring_view remote_root);
std::wstring RedactNetworkCredentials(std::wstring_view path);
bool WaitForNetworkRetry(HANDLE cancel_event, DWORD delay_ms);
DWORD SelectDirectoryWorkerLimit(bool network, uint32_t local_workers,
    uint32_t network_workers, DWORD processor_count);
NetworkRetryAction DecideNetworkRetry(NetworkFailureKind failure, uint32_t retries_used,
    uint32_t maximum_retries, bool cancelled);
uint64_t MaximumNetworkRetryDelay(uint32_t maximum_retries, DWORD base_delay_ms,
    DWORD maximum_delay_ms);
NetworkFailureDisposition NetworkFailureScope(NetworkFailureKind failure, bool root_item);
