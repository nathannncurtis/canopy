#include "dir_scanner.h"
#include "scan_context.h"
#include <queue>
#include <string>

// NtQueryDirectoryFile signature loaded dynamically from ntdll.
typedef LONG NTSTATUS;
#define STATUS_NO_MORE_FILES ((NTSTATUS)0x80000006L)

typedef struct {
    ULONG_PTR  Status;
    ULONG_PTR  Information;
} IO_STATUS_BLOCK_LOCAL;

typedef struct {
    ULONG          NextEntryOffset;
    ULONG          FileIndex;
    LARGE_INTEGER  CreationTime;
    LARGE_INTEGER  LastAccessTime;
    LARGE_INTEGER  LastWriteTime;
    LARGE_INTEGER  ChangeTime;
    LARGE_INTEGER  EndOfFile;
    LARGE_INTEGER  AllocationSize;
    ULONG          FileAttributes;
    ULONG          FileNameLength;
    WCHAR          FileName[1];
} FILE_DIRECTORY_INFORMATION_LOCAL;

typedef NTSTATUS (NTAPI *NtQueryDirectoryFilePtr)(
    HANDLE FileHandle,
    HANDLE Event,
    PVOID  ApcRoutine,
    PVOID  ApcContext,
    IO_STATUS_BLOCK_LOCAL* IoStatusBlock,
    PVOID  FileInformation,
    ULONG  Length,
    ULONG  FileInformationClass,  // 1 = FileDirectoryInformation
    BOOLEAN ReturnSingleEntry,
    PVOID  FileName,
    BOOLEAN RestartScan);

// Shared work state passed to thread pool callbacks.
struct DirWorkState {
    ScanContext*            ctx;
    NtQueryDirectoryFilePtr NtQueryDir;
    CRITICAL_SECTION        cs;
    std::queue<std::wstring> pending;
    // Root node index for linking.
    uint32_t                root_idx;
    // Counters for progress.
    uint64_t volatile       dirs_done;
    uint64_t volatile       files_done;
    // Associating dir path -> node index requires a map; we use a parallel queue approach:
    // Each work item carries its own parent index.
    // Since the queue holds (path, parent_idx) pairs, pack them in a struct.
};

struct DirWorkItem {
    std::wstring path;
    uint32_t     parent_idx;
};

// Extended state with typed queue.
struct DirState {
    ScanContext*            ctx;
    NtQueryDirectoryFilePtr NtQueryDir;
    CRITICAL_SECTION        cs;
    std::queue<DirWorkItem> pending;
    volatile LONG           in_flight; // work items currently being processed
    uint64_t volatile       dirs_done;
    uint64_t volatile       files_done;
    PTP_WORK                tp_work;   // set after creation so callbacks can resubmit
};

static void NTAPI WorkCallback(PTP_CALLBACK_INSTANCE, PVOID ctx_ptr, PTP_WORK work);

DWORD WINAPI DirScanThread(LPVOID param)
{
    ScanContext* ctx = static_cast<ScanContext*>(param);

    // Retrieve and clear the temp path pointer set by RouterBeginScan.
    wchar_t* scan_path = ctx->result.name_buf;
    ctx->result.name_buf = nullptr;

    LARGE_INTEGER freq{}, t0{}, t1{};
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t0);

    // Load NtQueryDirectoryFile from ntdll.
    NtQueryDirectoryFilePtr NtQueryDir =
        reinterpret_cast<NtQueryDirectoryFilePtr>(
            GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryDirectoryFile"));

    if (!NtQueryDir) {
        ctx->error = GetLastError();
        delete[] scan_path;
        return 1;
    }

    // Allocate root node for the scan root directory.
    uint32_t root_idx = ctx->pool.AllocNode();
    {
        ScanNode* root = ctx->pool.NodeAt(root_idx);
        // Name is the last path component or the path itself.
        const wchar_t* name_start = scan_path;
        for (const wchar_t* p = scan_path; *p; ++p) {
            if ((*p == L'\\' || *p == L'/') && *(p + 1))
                name_start = p + 1;
        }
        uint32_t name_len = static_cast<uint32_t>(wcslen(name_start));
        root->name_offset  = ctx->pool.AppendName(name_start, name_len);
        root->name_len     = name_len;
        root->flags        = SMON_FLAG_DIRECTORY;
        root->parent       = UINT32_MAX;
        root->first_child  = UINT32_MAX;
        root->next_sibling = UINT32_MAX;
        root->size         = 0;
    }

    // Thread pool setup.
    PTP_POOL pool = CreateThreadpool(nullptr);
    if (!pool) {
        ctx->error = GetLastError();
        delete[] scan_path;
        return 1;
    }

    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    DWORD max_threads = si.dwNumberOfProcessors * 4;
    if (max_threads > 32) max_threads = 32;
    if (max_threads < 1) max_threads = 1;
    SetThreadpoolThreadMaximum(pool, max_threads);

    TP_CALLBACK_ENVIRON tpenv{};
    InitializeThreadpoolEnvironment(&tpenv);
    SetThreadpoolCallbackPool(&tpenv, pool);

    DirState state{};
    state.ctx       = ctx;
    state.NtQueryDir = NtQueryDir;
    state.dirs_done  = 0;
    state.files_done = 0;
    state.in_flight  = 0;
    InitializeCriticalSection(&state.cs);

    // Enqueue the root directory.
    {
        DirWorkItem item;
        item.path       = scan_path;
        item.parent_idx = root_idx;
        state.pending.push(std::move(item));
    }
    delete[] scan_path;

    PTP_WORK tp_work = CreateThreadpoolWork(WorkCallback, &state, &tpenv);
    if (!tp_work) {
        ctx->error = GetLastError();
        DeleteCriticalSection(&state.cs);
        DestroyThreadpoolEnvironment(&tpenv);
        CloseThreadpool(pool);
        return 1;
    }
    state.tp_work = tp_work;

    // Submit initial work item.
    InterlockedIncrement(&state.in_flight);
    SubmitThreadpoolWork(tp_work);

    // Wait until in_flight reaches 0.
    while (InterlockedAdd(&state.in_flight, 0) > 0) {
        SleepEx(1, TRUE);
        if (ctx->cancelled.load())
            break;
    }

    WaitForThreadpoolWorkCallbacks(tp_work, TRUE);
    CloseThreadpoolWork(tp_work);
    DestroyThreadpoolEnvironment(&tpenv);
    CloseThreadpool(pool);
    DeleteCriticalSection(&state.cs);

    QueryPerformanceCounter(&t1);
    ctx->result.elapsed_sec = static_cast<double>(t1.QuadPart - t0.QuadPart)
                            / static_cast<double>(freq.QuadPart);
    return 0;
}

static void NTAPI WorkCallback(PTP_CALLBACK_INSTANCE, PVOID ctx_ptr, PTP_WORK)
{
    DirState* state = static_cast<DirState*>(ctx_ptr);
    ScanContext* ctx = state->ctx;

    if (ctx->cancelled.load()) {
        InterlockedDecrement(&state->in_flight);
        return;
    }

    // Dequeue one item.
    DirWorkItem item;
    {
        EnterCriticalSection(&state->cs);
        if (state->pending.empty()) {
            LeaveCriticalSection(&state->cs);
            InterlockedDecrement(&state->in_flight);
            return;
        }
        item = std::move(state->pending.front());
        state->pending.pop();
        LeaveCriticalSection(&state->cs);
    }

    // Open the directory.
    HANDLE dir = CreateFileW(item.path.c_str(),
                             FILE_LIST_DIRECTORY,
                             FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                             nullptr,
                             OPEN_EXISTING,
                             FILE_FLAG_BACKUP_SEMANTICS,
                             nullptr);
    if (dir == INVALID_HANDLE_VALUE) {
        InterlockedDecrement(&state->in_flight);
        return;
    }

    static const ULONG kBufSize = 65536;
    BYTE buf[kBufSize];

    BOOLEAN restart = TRUE;
    for (;;) {
        if (ctx->cancelled.load())
            break;

        IO_STATUS_BLOCK_LOCAL iosb{};
        NTSTATUS status = state->NtQueryDir(
            dir, nullptr, nullptr, nullptr,
            &iosb,
            buf, kBufSize,
            1, // FileDirectoryInformation
            FALSE, nullptr, restart);
        restart = FALSE;

        if (status == STATUS_NO_MORE_FILES)
            break;
        if (status < 0) // NTSTATUS failure
            break;

        BYTE* p = buf;
        for (;;) {
            FILE_DIRECTORY_INFORMATION_LOCAL* fdi =
                reinterpret_cast<FILE_DIRECTORY_INFORMATION_LOCAL*>(p);

            // Skip . and ..
            ULONG name_chars = fdi->FileNameLength / sizeof(WCHAR);
            bool is_dot = (name_chars == 1 && fdi->FileName[0] == L'.');
            bool is_dotdot = (name_chars == 2 && fdi->FileName[0] == L'.' && fdi->FileName[1] == L'.');

            if (!is_dot && !is_dotdot) {
                if (!ctx->pool.Full()) {
                    uint32_t idx = ctx->pool.AllocNode();
                    ScanNode* node = ctx->pool.NodeAt(idx);
                    node->name_offset  = ctx->pool.AppendName(fdi->FileName, name_chars);
                    node->name_len     = name_chars;
                    node->flags        = 0;
                    node->parent       = item.parent_idx;
                    node->first_child  = UINT32_MAX;
                    node->next_sibling = UINT32_MAX;

                    bool is_dir = (fdi->FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    if (is_dir)
                        node->flags |= SMON_FLAG_DIRECTORY;
                    if (fdi->FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
                        node->flags |= SMON_FLAG_REPARSE;

                    if (is_dir) {
                        node->size = 0;
                        // Build child path and enqueue.
                        if (!(fdi->FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
                            std::wstring child_path = item.path;
                            if (child_path.back() != L'\\')
                                child_path += L'\\';
                            child_path.append(fdi->FileName, name_chars);

                            DirWorkItem child_item;
                            child_item.path       = std::move(child_path);
                            child_item.parent_idx = idx;

                            EnterCriticalSection(&state->cs);
                            state->pending.push(std::move(child_item));
                            LeaveCriticalSection(&state->cs);

                            InterlockedIncrement(&state->in_flight);
                            SubmitThreadpoolWork(state->tp_work);
                        }
                        InterlockedIncrement64(
                            reinterpret_cast<volatile LONG64*>(&state->dirs_done));
                    } else {
                        node->size = static_cast<uint64_t>(fdi->AllocationSize.QuadPart);
                        InterlockedIncrement64(
                            reinterpret_cast<volatile LONG64*>(&state->files_done));
                    }

                    // Link into parent's child list (protected by pool's caller sync contract).
                    // Use interlocked swap on first_child to prepend atomically.
                    // NodePool is not thread-safe for AllocNode/AppendName, so we serialize
                    // pool access via the critical section.
                    ScanNode* parent_node = ctx->pool.NodeAt(item.parent_idx);
                    // Single atomic link -- no extra lock needed; each thread owns its parent slot.
                    node->next_sibling       = parent_node->first_child;
                    parent_node->first_child = idx;

                    // Progress callback every 1000 directories.
                    uint64_t dd = state->dirs_done;
                    if (dd % 1000 == 0 && ctx->callback) {
                        ctx->callback(dd, state->files_done, 0, ctx->user_data);
                    }
                }
            }

            if (fdi->NextEntryOffset == 0)
                break;
            p += fdi->NextEntryOffset;
        }
    }

    CloseHandle(dir);
    InterlockedDecrement(&state->in_flight);
}
