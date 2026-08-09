#include "../src/filesystem_identity.h"
#include <cstdio>

static bool Check(bool value, const wchar_t* message)
{
    if (value) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

int wmain()
{
    FileAllocationTracker tracker;
    FILE_STANDARD_INFO standard{};
    standard.NumberOfLinks = 3;
    standard.EndOfFile.QuadPart = 100;
    standard.AllocationSize.QuadPart = 4096;
    SmonNodeMetadata metadata = BuildMftNodeMetadata(7, 42, standard, false);
    return Check(tracker.Account(1, 42), L"first identity accounted") &&
        Check(!tracker.Account(1, 42), L"same-volume alias deduplicated") &&
        Check(tracker.Account(2, 42), L"same file number on another volume remains unique") &&
        Check(metadata.volume_serial == 7 && metadata.file_id == 42 && metadata.link_count == 3,
              L"MFT metadata preserves volume-scoped FRN and actual link count") &&
        Check((metadata.flags & SMON_NODE_META_CANONICAL_LINK_ONLY) != 0,
              L"MFT multi-link alias visibility is explicit") &&
        Check(metadata.logical_bytes == 100 && metadata.allocated_bytes == 4096 &&
              metadata.uniquely_accounted_bytes == 4096, L"MFT size semantics are explicit") ? 0 : 1;
}
