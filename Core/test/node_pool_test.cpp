#include "../src/node_pool.h"
#include "../src/tree_compactor.h"
#include <cstdio>
#include <cstring>

static bool Check(bool condition, const wchar_t* message)
{
    if (condition) return true;
    fwprintf(stderr, L"FAIL: %s\n", message);
    return false;
}

static uint32_t AddNode(NodePool& pool, const wchar_t* name,
                        uint32_t parent, uint32_t child, uint32_t sibling)
{
    uint32_t index = pool.AllocNode();
    if (index == UINT32_MAX) return index;
    ScanNode* node = pool.NodeAt(index);
    node->parent = parent;
    node->first_child = child;
    node->next_sibling = sibling;
    node->name_len = static_cast<uint32_t>(wcslen(name));
    node->name_offset = pool.AppendName(name, node->name_len);
    return node->name_offset == UINT32_MAX ? UINT32_MAX : index;
}

int wmain()
{
    NodePool pool;
    if (!Check(!pool.Full(), L"pool reserves its arena")) return 1;

    pool.TestSetUsage(NodePool::MaxNodes, 0);
    if (!Check(pool.Full(), L"pool full at exact node capacity") ||
        !Check(pool.AllocNode() == UINT32_MAX, L"node allocation rejected at capacity"))
        return 1;
    pool.TestSetUsage(0, NodePool::MaxNameBytes);
    if (!Check(pool.AppendName(L"x", 1) == UINT32_MAX,
               L"name allocation rejected at byte capacity"))
        return 1;
    pool.TestSetUsage(0, 0);

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

    NodePool tree;
    if (!Check(AddNode(tree, L"root", UINT32_MAX, 1, UINT32_MAX) == 0, L"tree root") ||
        !Check(AddNode(tree, L"one", 0, UINT32_MAX, 2) == 1, L"first child") ||
        !Check(AddNode(tree, L"two", 0, UINT32_MAX, UINT32_MAX) == 2, L"second child") ||
        !Check(AddNode(tree, L"unrelated", UINT32_MAX, UINT32_MAX, UINT32_MAX) == 3,
               L"unrelated root"))
        return 1;
    std::vector<uint64_t> payload{10, 11, 12, 13};
    if (!Check(CompactSubtree(tree, 0, payload) == ERROR_SUCCESS, L"compact subtree") ||
        !Check(payload == std::vector<uint64_t>({10, 11, 12}), L"payload remapped"))
        return 1;
    ScanResult compacted{};
    tree.Finalize(&compacted);
    if (!Check(compacted.node_count == 3, L"unrelated nodes removed") ||
        !Check(compacted.nodes[0].parent == UINT32_MAX, L"compacted root") ||
        !Check(compacted.nodes[0].first_child == 1, L"first child remapped") ||
        !Check(compacted.nodes[1].next_sibling == 2, L"sibling remapped") ||
        !Check(compacted.nodes[2].parent == 0, L"parent remapped"))
        return 1;

    NodePool cyclic;
    AddNode(cyclic, L"root", UINT32_MAX, 1, UINT32_MAX);
    AddNode(cyclic, L"child", 0, UINT32_MAX, 1);
    std::vector<uint64_t> cyclic_payload{1, 2};
    if (!Check(CompactSubtree(cyclic, 0, cyclic_payload) == ERROR_INVALID_DATA,
               L"sibling cycle rejected"))
        return 1;

    return 0;
}
