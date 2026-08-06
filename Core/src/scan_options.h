#pragma once
#include <windows.h>
#include <stdint.h>
#include <string>
#include <string_view>
#include <vector>

struct ScanOptions {
    uint32_t max_depth = UINT32_MAX;
    uint32_t worker_threads = 0; // 0 = automatic
    uint64_t minimum_file_size = 0;
    uint64_t maximum_file_size = UINT64_MAX;
    bool include_hidden = true;
    bool include_system = true;
    bool include_temporary = true;
    bool include_reparse_points = true;
    bool follow_reparse_points = false;
    bool stay_on_volume = true;
    bool force_directory_scanner = false;
    std::vector<std::wstring> excluded_patterns;
    std::vector<std::wstring> excluded_extensions;

    DWORD Validate() const;
    bool HasConstrainingOptions() const;
    bool ShouldInclude(std::wstring_view relative_path,
                       std::wstring_view name,
                       DWORD attributes,
                       uint64_t size,
                       bool is_directory,
                       uint32_t depth) const;

    void SetExcludedPatterns(std::wstring_view list);
    void SetExcludedExtensions(std::wstring_view list);
};

bool GlobMatchInsensitive(std::wstring_view pattern, std::wstring_view value);
