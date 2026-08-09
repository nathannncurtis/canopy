#include "scan_options.h"
#include <algorithm>
#include <cwctype>
#include <windows.h>

static bool SameCharacter(wchar_t left, wchar_t right)
{
    if ((left == L'/' || left == L'\\') && (right == L'/' || right == L'\\'))
        return true;
    return CompareStringOrdinal(&left, 1, &right, 1, TRUE) == CSTR_EQUAL;
}

bool GlobMatchInsensitive(std::wstring_view pattern, std::wstring_view value)
{
    size_t pattern_index = 0;
    size_t value_index = 0;
    size_t star_index = std::wstring_view::npos;
    size_t star_value_index = 0;

    while (value_index < value.size()) {
        if (pattern_index < pattern.size() &&
            (pattern[pattern_index] == L'?' ||
             SameCharacter(pattern[pattern_index], value[value_index]))) {
            ++pattern_index;
            ++value_index;
        } else if (pattern_index < pattern.size() && pattern[pattern_index] == L'*') {
            star_index = pattern_index++;
            star_value_index = value_index;
        } else if (star_index != std::wstring_view::npos) {
            pattern_index = star_index + 1;
            value_index = ++star_value_index;
        } else {
            return false;
        }
    }
    while (pattern_index < pattern.size() && pattern[pattern_index] == L'*')
        ++pattern_index;
    return pattern_index == pattern.size();
}

static std::wstring_view Trim(std::wstring_view value)
{
    while (!value.empty() && iswspace(value.front())) value.remove_prefix(1);
    while (!value.empty() && iswspace(value.back())) value.remove_suffix(1);
    return value;
}

static std::vector<std::wstring> SplitList(std::wstring_view list, bool extensions)
{
    std::vector<std::wstring> values;
    size_t start = 0;
    while (start <= list.size()) {
        size_t end = list.find_first_of(L";,\r\n", start);
        std::wstring_view item = Trim(list.substr(
            start, end == std::wstring_view::npos ? list.size() - start : end - start));
        if (!item.empty()) {
            std::wstring normalized(item);
            if (extensions && normalized.starts_with(L"*.")) normalized.erase(normalized.begin());
            if (extensions && normalized.front() != L'.') normalized.insert(normalized.begin(), L'.');
            if (!normalized.empty()) CharUpperBuffW(normalized.data(), static_cast<DWORD>(normalized.size()));
            values.push_back(std::move(normalized));
        }
        if (end == std::wstring_view::npos) break;
        start = end + 1;
    }
    std::sort(values.begin(), values.end());
    values.erase(std::unique(values.begin(), values.end()), values.end());
    return values;
}

void ScanOptions::SetExcludedPatterns(std::wstring_view list)
{
    excluded_patterns = SplitList(list, false);
}

void ScanOptions::SetExcludedExtensions(std::wstring_view list)
{
    excluded_extensions = SplitList(list, true);
}

DWORD ScanOptions::Validate() const
{
    if (minimum_file_size > maximum_file_size) return ERROR_INVALID_PARAMETER;
    if (worker_threads > 32) return ERROR_INVALID_PARAMETER;
    if (network_worker_threads < 1 || network_worker_threads > 16 ||
        network_retry_count > 5 || network_retry_delay_ms > 5000) return ERROR_INVALID_PARAMETER;
    return ERROR_SUCCESS;
}

bool ScanOptions::HasConstrainingOptions() const
{
    return max_depth != UINT32_MAX || minimum_file_size != 0 ||
        maximum_file_size != UINT64_MAX || !include_hidden || !include_system ||
        !include_temporary || !include_reparse_points ||
        include_alternate_streams || follow_reparse_points || !stay_on_volume ||
        !excluded_patterns.empty() || !excluded_extensions.empty();
}

bool ScanOptions::ShouldInclude(std::wstring_view relative_path,
                                std::wstring_view name,
                                DWORD attributes,
                                uint64_t size,
                                bool is_directory,
                                uint32_t depth) const
{
    if (depth > max_depth) return false;
    if (!include_hidden && (attributes & FILE_ATTRIBUTE_HIDDEN)) return false;
    if (!include_system && (attributes & FILE_ATTRIBUTE_SYSTEM)) return false;
    if (!include_temporary && (attributes & FILE_ATTRIBUTE_TEMPORARY)) return false;
    if (!include_reparse_points && (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return false;

    for (const std::wstring& pattern : excluded_patterns) {
        if (GlobMatchInsensitive(pattern, relative_path) || GlobMatchInsensitive(pattern, name))
            return false;
    }

    if (!is_directory) {
        if (size < minimum_file_size || size > maximum_file_size) return false;
        size_t dot = name.find_last_of(L'.');
        std::wstring extension = dot == std::wstring_view::npos
            ? std::wstring()
            : std::wstring(name.substr(dot));
        for (const std::wstring& excluded : excluded_extensions) {
            if (extension.size() == excluded.size() &&
                CompareStringOrdinal(extension.data(), static_cast<int>(extension.size()),
                    excluded.data(), static_cast<int>(excluded.size()), TRUE) == CSTR_EQUAL)
                return false;
        }
    }
    return true;
}
