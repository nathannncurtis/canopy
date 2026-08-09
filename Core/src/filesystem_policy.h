#pragma once
#include <windows.h>
#include <stdint.h>
#include <string>
#include <string_view>
enum class FilesystemKind : uint32_t { Unknown, Ntfs, Refs, Fat, Fat32, Exfat, Other, Network };
FilesystemKind ClassifyFilesystem(std::wstring_view name);
bool IsCloudPlaceholderAttributes(DWORD attributes);
std::wstring NormalizeExtendedPath(std::wstring_view path);
