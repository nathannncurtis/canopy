#include "../src/scan_options.h"
#include <cstdio>

static bool Check(bool condition, const wchar_t* message)
{
    if (condition) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

int wmain()
{
    if (!Check(GlobMatchInsensitive(L"*.TMP", L"cache.tmp"), L"case-insensitive glob") ||
        !Check(GlobMatchInsensitive(L"build/*/obj", L"BUILD\\x64\\obj"), L"path glob") ||
        !Check(GlobMatchInsensitive(L"file-??.bin", L"file-01.bin"), L"question glob") ||
        !Check(!GlobMatchInsensitive(L"*.zip", L"archive.7z"), L"glob mismatch"))
        return 1;

    ScanOptions defaults;
    if (!Check(defaults.Validate() == ERROR_SUCCESS, L"default options valid") ||
        !Check(defaults.ShouldInclude(L"hidden.dat", L"hidden.dat", FILE_ATTRIBUTE_HIDDEN,
                                      10, false, 1), L"defaults preserve current behavior"))
        return 1;

    ScanOptions filtered;
    filtered.max_depth = 2;
    filtered.minimum_file_size = 100;
    filtered.maximum_file_size = 1000;
    filtered.include_hidden = false;
    filtered.include_system = false;
    filtered.include_temporary = false;
    filtered.include_reparse_points = false;
    filtered.SetExcludedPatterns(L"obj; build\\* ; OBJ");
    filtered.SetExcludedExtensions(L"tmp, .LOG;tmp");

    if (!Check(!filtered.ShouldInclude(L"deep\\file.bin", L"file.bin", 0, 500, false, 3),
               L"depth excluded") ||
        !Check(!filtered.ShouldInclude(L"secret", L"secret", FILE_ATTRIBUTE_HIDDEN, 0, true, 1),
               L"hidden excluded") ||
        !Check(!filtered.ShouldInclude(L"build\\x64", L"x64", FILE_ATTRIBUTE_DIRECTORY,
                                      0, true, 1), L"directory pattern excluded") ||
        !Check(!filtered.ShouldInclude(L"cache.tmp", L"cache.tmp", 0, 500, false, 1),
               L"extension excluded") ||
        !Check(!filtered.ShouldInclude(L"small.bin", L"small.bin", 0, 99, false, 1),
               L"minimum size excluded") ||
        !Check(filtered.ShouldInclude(L"data.bin", L"data.bin", 0, 500, false, 1),
               L"matching file included") ||
        !Check(filtered.excluded_patterns.size() == 2, L"patterns normalized and deduplicated") ||
        !Check(filtered.excluded_extensions.size() == 2, L"extensions normalized and deduplicated"))
        return 1;

    filtered.minimum_file_size = 2;
    filtered.maximum_file_size = 1;
    if (!Check(filtered.Validate() == ERROR_INVALID_PARAMETER, L"invalid size range rejected"))
        return 1;
    filtered.maximum_file_size = UINT64_MAX;
    filtered.worker_threads = 1025;
    if (!Check(filtered.Validate() == ERROR_INVALID_PARAMETER, L"thread cap enforced"))
        return 1;
    return 0;
}
