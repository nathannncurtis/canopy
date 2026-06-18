#include "mft_scanner.h"
#include "scan_context.h"
#include <unordered_map>
#include <cstring>

// IOCTL codes for USN journal access (stable numeric values).
#ifndef FSCTL_QUERY_USN_JOURNAL
#  define FSCTL_QUERY_USN_JOURNAL   0x000900F4UL
#endif
#ifndef FSCTL_ENUM_USN_DATA
#  define FSCTL_ENUM_USN_DATA       0x000900B3UL
#endif

typedef LONGLONG USN;

typedef struct {
    DWORDLONG UsnJournalID;
    USN       FirstUsn;
    USN       NextUsn;
    USN       LowestValidUsn;
    USN       MaxUsn;
    DWORDLONG MaximumSize;
    DWORDLONG AllocationDelta;
} USN_JOURNAL_DATA_V0_LOCAL;

typedef struct {
    DWORDLONG StartFileReferenceNumber;
    USN       LowUsn;
    USN       HighUsn;
} MFT_ENUM_DATA_V0_LOCAL;

typedef struct {
    DWORD     RecordLength;
    WORD      MajorVersion;
    WORD      MinorVersion;
    DWORDLONG FileReferenceNumber;
    DWORDLONG ParentFileReferenceNumber;
    USN       Usn;
    LARGE_INTEGER TimeStamp;
    DWORD     Reason;
    DWORD     SourceInfo;
    DWORD     SecurityId;
    DWORD     FileAttributes;
    WORD      FileNameLength;
    WORD      FileNameOffset;
    WCHAR     FileName[1];
} USN_RECORD_V2_LOCAL;

static wchar_t* GetPathFromContext(ScanContext* ctx)
{
    // name_buf is temporarily used to carry the scan path (set by router).
    return ctx->result.name_buf;
}

DWORD WINAPI MftScanThread(LPVOID param)
{
    ScanContext* ctx = static_cast<ScanContext*>(param);

    // Retrieve and clear the temp path pointer set by RouterBeginScan.
    wchar_t* scan_path = GetPathFromContext(ctx);
    ctx->result.name_buf = nullptr;

    LARGE_INTEGER freq{}, t0{}, t1{};
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t0);

    // Build volume path: "\\.\C:" from "C:\..."
    wchar_t vol_path[8] = {};
    vol_path[0] = L'\\';
    vol_path[1] = L'\\';
    vol_path[2] = L'.';
    vol_path[3] = L'\\';
    vol_path[4] = scan_path[0]; // drive letter
    vol_path[5] = L':';
    vol_path[6] = L'\0';

    delete[] scan_path;

    HANDLE vol = CreateFileW(vol_path,
                             GENERIC_READ,
                             FILE_SHARE_READ | FILE_SHARE_WRITE,
                             nullptr,
                             OPEN_EXISTING,
                             FILE_FLAG_BACKUP_SEMANTICS,
                             nullptr);
    if (vol == INVALID_HANDLE_VALUE) {
        ctx->error = GetLastError();
        return 1;
    }

    // Query journal metadata to get MaxUsn.
    USN_JOURNAL_DATA_V0_LOCAL jd{};
    DWORD bytes_ret = 0;
    if (!DeviceIoControl(vol,
                         FSCTL_QUERY_USN_JOURNAL,
                         nullptr, 0,
                         &jd, sizeof(jd),
                         &bytes_ret, nullptr)) {
        ctx->error = GetLastError();
        CloseHandle(vol);
        return 1;
    }

    // Maps FRN -> node pool index for parent linking.
    std::unordered_map<DWORDLONG, uint32_t> frn_map;
    frn_map.reserve(1 << 18); // pre-size for ~260k entries

    MFT_ENUM_DATA_V0_LOCAL med{};
    med.StartFileReferenceNumber = 0;
    med.LowUsn  = 0;
    med.HighUsn = jd.MaxUsn;

    static const uint32_t kBufSize = 65536;
    BYTE buf[kBufSize];

    uint64_t record_count = 0;

    for (;;) {
        if (ctx->cancelled.load())
            break;

        DWORD out_bytes = 0;
        BOOL ok = DeviceIoControl(vol,
                                  FSCTL_ENUM_USN_DATA,
                                  &med, sizeof(med),
                                  buf, kBufSize,
                                  &out_bytes, nullptr);
        if (!ok) {
            DWORD err = GetLastError();
            if (err == ERROR_HANDLE_EOF)
                break; // enumeration complete
            ctx->error = err;
            CloseHandle(vol);
            return 1;
        }

        if (out_bytes < sizeof(DWORDLONG))
            break;

        // First 8 bytes of output are the next StartFileReferenceNumber.
        DWORDLONG next_frn;
        memcpy(&next_frn, buf, sizeof(DWORDLONG));
        med.StartFileReferenceNumber = next_frn;

        BYTE* p   = buf + sizeof(DWORDLONG);
        BYTE* end = buf + out_bytes;

        while (p < end) {
            if (p + sizeof(USN_RECORD_V2_LOCAL) > end)
                break;

            USN_RECORD_V2_LOCAL* rec = reinterpret_cast<USN_RECORD_V2_LOCAL*>(p);
            if (rec->RecordLength == 0)
                break;

            if (ctx->pool.Full()) {
                ctx->error = ERROR_INSUFFICIENT_BUFFER;
                CloseHandle(vol);
                return 1;
            }

            uint32_t idx = ctx->pool.AllocNode();
            ScanNode* node = ctx->pool.NodeAt(idx);

            // Name
            WORD name_len_chars = rec->FileNameLength / sizeof(WCHAR);
            WCHAR* name_ptr = reinterpret_cast<WCHAR*>(
                reinterpret_cast<BYTE*>(rec) + rec->FileNameOffset);
            node->name_offset = ctx->pool.AppendName(name_ptr, name_len_chars);
            node->name_len    = name_len_chars;

            // Flags
            node->flags = 0;
            if (rec->FileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                node->flags |= SMON_FLAG_DIRECTORY;
            if (rec->FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
                node->flags |= SMON_FLAG_REPARSE;

            // Leave size = 0 for now; MFT enumeration doesn't give file sizes.
            node->size        = 0;
            node->parent      = UINT32_MAX;
            node->first_child = UINT32_MAX;
            node->next_sibling = UINT32_MAX;

            frn_map[rec->FileReferenceNumber] = idx;

            // Store ParentFRN temporarily in the size field (64-bit, safe).
            // We'll fix it in the second pass.
            memcpy(&node->size, &rec->ParentFileReferenceNumber, sizeof(DWORDLONG));

            ++record_count;
            if (record_count % 50000 == 0 && ctx->callback) {
                ctx->callback(0, record_count, 0, ctx->user_data);
            }

            p += rec->RecordLength;
        }
    }

    CloseHandle(vol);

    // Second pass: resolve parent FRNs and build child/sibling chains.
    uint32_t node_count = static_cast<uint32_t>(frn_map.size());
    // Iterate all allocated nodes by index range (pool tracks count via Finalize later).
    // node_count equals record_count capped by pool.
    for (uint32_t i = 0; i < node_count; ++i) {
        ScanNode* node = ctx->pool.NodeAt(i);

        DWORDLONG parent_frn = 0;
        memcpy(&parent_frn, &node->size, sizeof(DWORDLONG));
        node->size = 0; // clear the temp field

        auto it = frn_map.find(parent_frn);
        if (it == frn_map.end())
            continue; // no parent found, leave as root (parent = UINT32_MAX)

        uint32_t parent_idx = it->second;
        if (parent_idx == i)
            continue; // self-referential root entry

        node->parent = parent_idx;

        // Prepend to parent's child list.
        ScanNode* parent_node = ctx->pool.NodeAt(parent_idx);
        node->next_sibling        = parent_node->first_child;
        parent_node->first_child  = i;
    }

    QueryPerformanceCounter(&t1);
    ctx->result.elapsed_sec = static_cast<double>(t1.QuadPart - t0.QuadPart)
                            / static_cast<double>(freq.QuadPart);
    return 0;
}
