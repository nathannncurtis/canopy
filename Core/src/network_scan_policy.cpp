#include "network_scan_policy.h"
#include <algorithm>

NetworkFailureKind ClassifyNetworkError(DWORD error)
{
    switch (error) {
    case ERROR_SUCCESS: return NetworkFailureKind::None;
    case ERROR_BAD_NETPATH: case ERROR_NETWORK_UNREACHABLE: case ERROR_CONNECTION_UNAVAIL:
    case ERROR_NETNAME_DELETED: case ERROR_NO_NETWORK: return NetworkFailureKind::Disconnected;
    case ERROR_ACCESS_DENIED: case ERROR_LOGON_FAILURE: case ERROR_BAD_USERNAME:
    case ERROR_SESSION_CREDENTIAL_CONFLICT: return NetworkFailureKind::Authentication;
    case ERROR_SEM_TIMEOUT: case WAIT_TIMEOUT: return NetworkFailureKind::Timeout;
    case ERROR_BAD_NET_NAME: case ERROR_NO_NET_OR_BAD_PATH: return NetworkFailureKind::NameResolution;
    case ERROR_FILE_NOT_FOUND: case ERROR_PATH_NOT_FOUND: return NetworkFailureKind::PathMissing;
    default: return NetworkFailureKind::Other;
    }
}

bool IsRetryableNetworkFailure(NetworkFailureKind failure)
{
    return failure == NetworkFailureKind::Timeout || failure == NetworkFailureKind::Disconnected;
}

DWORD NetworkRetryDelay(uint32_t attempt, DWORD base_delay_ms, DWORD maximum_delay_ms)
{
    uint64_t delay = base_delay_ms;
    for (uint32_t i = 0; i < attempt && delay < maximum_delay_ms; ++i)
        delay = std::min<uint64_t>(delay * 2, maximum_delay_ms);
    return static_cast<DWORD>(std::min<uint64_t>(delay, maximum_delay_ms));
}

bool IsUncPath(std::wstring_view path)
{
    return path.starts_with(L"\\\\") || path.starts_with(L"\\\\?\\UNC\\");
}

std::wstring CombineMappedRemotePath(std::wstring_view input, std::wstring_view remote_root)
{
    if (input.size() < 2 || input[1] != L':' || remote_root.empty()) return std::wstring(input);
    std::wstring result(remote_root);
    while (!result.empty() && (result.back() == L'\\' || result.back() == L'/')) result.pop_back();
    std::wstring_view suffix = input.substr(2);
    if (!suffix.empty() && suffix.front() != L'\\' && suffix.front() != L'/') result += L'\\';
    result.append(suffix);
    return result;
}

std::wstring RedactNetworkCredentials(std::wstring_view path)
{
    size_t scheme = path.find(L"://");
    if (scheme == std::wstring_view::npos) return std::wstring(path);
    size_t authority = scheme + 3;
    size_t slash = path.find(L'/', authority);
    size_t at = path.find(L'@', authority);
    if (at == std::wstring_view::npos || (slash != std::wstring_view::npos && at > slash))
        return std::wstring(path);
    std::wstring result(path.substr(0, authority));
    result += L"<redacted>@";
    result += path.substr(at + 1);
    return result;
}

bool WaitForNetworkRetry(HANDLE cancel_event, DWORD delay_ms)
{
    return cancel_event && WaitForSingleObject(cancel_event, delay_ms) == WAIT_TIMEOUT;
}

DWORD SelectDirectoryWorkerLimit(bool network, uint32_t local_workers,
    uint32_t network_workers, DWORD processor_count)
{
    DWORD selected = network ? network_workers
        : (local_workers != 0 ? local_workers : processor_count * 4);
    DWORD maximum = network ? 16 : 32;
    return std::clamp<DWORD>(selected, 1, maximum);
}

NetworkRetryAction DecideNetworkRetry(NetworkFailureKind failure, uint32_t retries_used,
    uint32_t maximum_retries, bool cancelled)
{
    if (cancelled) return NetworkRetryAction::Cancelled;
    if (failure == NetworkFailureKind::None) return NetworkRetryAction::Complete;
    if (IsRetryableNetworkFailure(failure) && retries_used < maximum_retries)
        return NetworkRetryAction::Retry;
    return NetworkRetryAction::Fail;
}

uint64_t MaximumNetworkRetryDelay(uint32_t maximum_retries, DWORD base_delay_ms,
    DWORD maximum_delay_ms)
{
    uint64_t total = 0;
    for (uint32_t retry = 0; retry < maximum_retries; ++retry)
        total += NetworkRetryDelay(retry, base_delay_ms, maximum_delay_ms);
    return total;
}

NetworkFailureDisposition NetworkFailureScope(NetworkFailureKind failure, bool root_item)
{
    // A transport disconnect invalidates the share as a whole. Other descendant
    // failures are isolated so successfully scanned siblings remain useful.
    return root_item || failure == NetworkFailureKind::Disconnected
        ? NetworkFailureDisposition::FatalScan
        : NetworkFailureDisposition::SkipDescendant;
}
