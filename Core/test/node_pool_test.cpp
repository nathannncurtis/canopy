#include "../src/node_pool.h"
#include <cstdio>
#include <cstring>

static bool Check(bool condition, const wchar_t* message)
{
    if (condition) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

int wmain()
{
    NodePool pool;
    if (!Check(!pool.Full(), L"pool reserves its arena")) return 1;

    uint32_t index = pool.AllocNode();
    if (!Check(index == 0, L"first node index") ||
        !Check(pool.NodeAt(index)->parent == 0, L"new nodes are zero initialized"))
        return 1;

    const wchar_t name[] = L"canopy";
    uint32_t offset = pool.AppendName(name, 6);
    if (!Check(offset == 0, L"first name offset")) return 1;

    ScanResult result{};
    pool.Finalize(&result);
    if (!Check(result.node_count == 1, L"finalized node count") ||
        !Check(std::memcmp(result.name_buf, name, 6 * sizeof(wchar_t)) == 0,
               L"finalized name contents") ||
        !Check(pool.AppendName(nullptr, 1) == UINT32_MAX, L"null name rejected") ||
        !Check(pool.AppendName(name, UINT32_MAX) == UINT32_MAX, L"overflowing name rejected"))
        return 1;

    return 0;
}
