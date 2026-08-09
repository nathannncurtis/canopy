#include "../src/network_scan_policy.h"
#include <cstdio>

static bool Check(bool value, const wchar_t* message)
{
    if (value) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

static uint32_t SimulateAttempts(const NetworkFailureKind* sequence, uint32_t count,
    uint32_t maximum_retries, uint32_t cancel_before_attempt)
{
    uint32_t attempts = 0;
    uint32_t retries = 0;
    while (attempts < count) {
        bool cancelled = attempts == cancel_before_attempt;
        NetworkRetryAction action = DecideNetworkRetry(sequence[attempts], retries,
            maximum_retries, cancelled);
        if (action == NetworkRetryAction::Cancelled) break;
        ++attempts;
        if (action != NetworkRetryAction::Retry) break;
        ++retries;
    }
    return attempts;
}

int wmain()
{
    const NetworkFailureKind transient_then_success[] = {
        NetworkFailureKind::Timeout, NetworkFailureKind::Disconnected, NetworkFailureKind::None };
    const NetworkFailureKind all_transient[] = {
        NetworkFailureKind::Timeout, NetworkFailureKind::Timeout, NetworkFailureKind::Timeout };
    const NetworkFailureKind auth_then_success[] = {
        NetworkFailureKind::Authentication, NetworkFailureKind::None };
    const NetworkFailureKind name_then_success[] = {
        NetworkFailureKind::NameResolution, NetworkFailureKind::None };
    const NetworkFailureKind path_then_success[] = {
        NetworkFailureKind::PathMissing, NetworkFailureKind::None };
    if (!Check(IsUncPath(L"\\\\server\\share"), L"UNC recognized") ||
        !Check(IsUncPath(L"\\\\?\\UNC\\server\\share"), L"extended UNC recognized") ||
        !Check(!IsUncPath(L"Z:\\folder"), L"mapped drive needs resolution") ||
        !Check(CombineMappedRemotePath(L"Z:\\folder\\file", L"\\\\server\\share") ==
               L"\\\\server\\share\\folder\\file", L"mapped suffix preserved") ||
        !Check(ClassifyNetworkError(ERROR_NETNAME_DELETED) == NetworkFailureKind::Disconnected,
               L"disconnect classified") ||
        !Check(ClassifyNetworkError(ERROR_LOGON_FAILURE) == NetworkFailureKind::Authentication,
               L"authentication classified") ||
        !Check(ClassifyNetworkError(ERROR_SEM_TIMEOUT) == NetworkFailureKind::Timeout,
               L"timeout classified") ||
        !Check(ClassifyNetworkError(ERROR_BAD_NET_NAME) == NetworkFailureKind::NameResolution,
               L"name resolution classified") ||
        !Check(ClassifyNetworkError(ERROR_PATH_NOT_FOUND) == NetworkFailureKind::PathMissing,
               L"missing path classified") ||
        !Check(!IsRetryableNetworkFailure(NetworkFailureKind::Authentication),
               L"authentication is not retried") ||
        !Check(NetworkRetryDelay(0, 100, 5000) == 100 &&
               NetworkRetryDelay(8, 100, 5000) == 5000, L"retry delay is bounded") ||
        !Check(SelectDirectoryWorkerLimit(true, 12, 1, 8) == 1,
               L"network concurrency one is deterministic and independent") ||
        !Check(SelectDirectoryWorkerLimit(false, 3, 16, 8) == 3,
               L"local concurrency ignores network policy") ||
        !Check(SelectDirectoryWorkerLimit(true, 1, 99, 8) == 16,
               L"network concurrency is defensively bounded") ||
        !Check(RedactNetworkCredentials(L"smb://alice:secret@server/share") ==
               L"smb://<redacted>@server/share", L"credentials redacted") ||
        !Check(SimulateAttempts(transient_then_success, 3, 2, UINT32_MAX) == 3,
               L"transient failures retry exactly then stop on success") ||
        !Check(SimulateAttempts(all_transient, 3, 1, UINT32_MAX) == 2,
               L"retry count is an exact upper bound") ||
        !Check(SimulateAttempts(auth_then_success, 2, 5, UINT32_MAX) == 1 &&
               SimulateAttempts(name_then_success, 2, 5, UINT32_MAX) == 1 &&
               SimulateAttempts(path_then_success, 2, 5, UINT32_MAX) == 1,
               L"auth, name, and missing path fail immediately") ||
        !Check(SimulateAttempts(transient_then_success, 3, 5, 1) == 1,
               L"cancellation prevents the next open attempt") ||
        !Check(MaximumNetworkRetryDelay(5, 100, 5000) == 3100,
               L"maximum aggregate retry delay is deterministic and bounded") ||
        !Check(NetworkFailureScope(NetworkFailureKind::Authentication, true) ==
               NetworkFailureDisposition::FatalScan, L"root authentication is fatal") ||
        !Check(NetworkFailureScope(NetworkFailureKind::Disconnected, false) ==
               NetworkFailureDisposition::FatalScan, L"share disconnect is fatal from descendants") ||
        !Check(NetworkFailureScope(NetworkFailureKind::Authentication, false) ==
               NetworkFailureDisposition::SkipDescendant, L"descendant access failure is skipped") ||
        !Check(NetworkFailureScope(NetworkFailureKind::PathMissing, false) ==
               NetworkFailureDisposition::SkipDescendant, L"disappearing descendant is skipped")) return 1;

    HANDLE cancel = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!cancel) return 1;
    SetEvent(cancel);
    bool cancelled_promptly = !WaitForNetworkRetry(cancel, 5000);
    ResetEvent(cancel);
    bool elapsed = WaitForNetworkRetry(cancel, 1);
    CloseHandle(cancel);
    return Check(cancelled_promptly, L"signaled cancellation interrupts backoff") &&
           Check(elapsed, L"unsignaled backoff expires deterministically") ? 0 : 1;
}
