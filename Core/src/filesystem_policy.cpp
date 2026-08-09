#include "filesystem_policy.h"
#include <cwctype>

static bool EqualInsensitive(std::wstring_view left, std::wstring_view right)
{
    return left.size() == right.size() &&
        CompareStringOrdinal(left.data(), static_cast<int>(left.size()),
            right.data(), static_cast<int>(right.size()), TRUE) == CSTR_EQUAL;
}

FilesystemKind ClassifyFilesystem(std::wstring_view name)
{
    if (EqualInsensitive(name, L"NTFS")) return FilesystemKind::Ntfs;
    if (EqualInsensitive(name, L"ReFS")) return FilesystemKind::Refs;
    if (EqualInsensitive(name, L"FAT")) return FilesystemKind::Fat;
    if (EqualInsensitive(name, L"FAT32")) return FilesystemKind::Fat32;
    if (EqualInsensitive(name, L"exFAT")) return FilesystemKind::Exfat;
    return name.empty() ? FilesystemKind::Unknown : FilesystemKind::Other;
}

bool IsCloudPlaceholderAttributes(DWORD attributes)
{
    constexpr DWORD RecallOnOpen = 0x00040000u;
    constexpr DWORD RecallOnDataAccess = 0x00400000u;
    return (attributes & (FILE_ATTRIBUTE_OFFLINE | RecallOnOpen | RecallOnDataAccess)) != 0;
}

std::wstring NormalizeExtendedPath(std::wstring_view path)
{
    if (path.empty()) return {};
    std::wstring value(path);
    for (wchar_t& character : value)
        if (character == L'/') character = L'\\';
    if (value.starts_with(L"\\\\?\\") || value.starts_with(L"\\\\.\\")) return value;

    DWORD needed = GetFullPathNameW(value.c_str(), 0, nullptr, nullptr);
    if (needed != 0) {
        std::wstring absolute(needed, L'\0');
        DWORD written = GetFullPathNameW(value.c_str(), needed, absolute.data(), nullptr);
        if (written != 0 && written < needed) {
            absolute.resize(written);
            value = std::move(absolute);
        }
    }
    if (value.starts_with(L"\\\\")) return L"\\\\?\\UNC\\" + value.substr(2);
    if (value.size() >= 2 && iswalpha(value[0]) && value[1] == L':') return L"\\\\?\\" + value;
    return value;
}
