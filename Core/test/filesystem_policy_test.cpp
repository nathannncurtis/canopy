#include "../src/filesystem_policy.h"
#include <cstdio>

static bool Check(bool value, const wchar_t* message)
{
    if (value) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

int wmain()
{
    if (!Check(ClassifyFilesystem(L"ntfs") == FilesystemKind::Ntfs, L"NTFS classification") ||
        !Check(ClassifyFilesystem(L"ReFS") == FilesystemKind::Refs, L"ReFS classification") ||
        !Check(ClassifyFilesystem(L"FAT") == FilesystemKind::Fat, L"FAT classification") ||
        !Check(ClassifyFilesystem(L"fat32") == FilesystemKind::Fat32, L"FAT32 classification") ||
        !Check(ClassifyFilesystem(L"exFAT") == FilesystemKind::Exfat, L"exFAT classification") ||
        !Check(ClassifyFilesystem(L"CDFS") == FilesystemKind::Other, L"unsupported classification") ||
        !Check(IsCloudPlaceholderAttributes(FILE_ATTRIBUTE_OFFLINE), L"offline placeholder") ||
        !Check(IsCloudPlaceholderAttributes(0x00400000u), L"recall-on-access placeholder") ||
        !Check(!IsCloudPlaceholderAttributes(FILE_ATTRIBUTE_ARCHIVE), L"ordinary file is not cloud-backed")) return 1;
    std::wstring local = NormalizeExtendedPath(L"C:/data/file");
    std::wstring unc = NormalizeExtendedPath(L"\\\\server\\share\\folder");
    if (!Check(local == L"\\\\?\\C:\\data\\file", L"local extended normalization") ||
        !Check(unc == L"\\\\?\\UNC\\server\\share\\folder", L"UNC extended normalization") ||
        !Check(NormalizeExtendedPath(local) == local, L"normalization is idempotent")) return 1;
    return 0;
}
