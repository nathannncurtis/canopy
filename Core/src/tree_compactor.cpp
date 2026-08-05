#include "tree_compactor.h"
#include <algorithm>

DWORD CompactSubtree(NodePool& pool, uint32_t root, std::vector<uint64_t>& payload)
{
    ScanResult source{};
    pool.Finalize(&source);
    if (root >= source.node_count || payload.size() < source.node_count)
        return ERROR_INVALID_DATA;

    std::vector<uint8_t> visited(source.node_count, 0);
    std::vector<uint32_t> order;
    std::vector<uint32_t> stack{root};
    visited[root] = 1;

    while (!stack.empty()) {
        uint32_t current = stack.back();
        stack.pop_back();
        order.push_back(current);

        std::vector<uint32_t> children;
        uint32_t child = source.nodes[current].first_child;
        uint32_t sibling_steps = 0;
        while (child != UINT32_MAX) {
            if (child >= source.node_count || ++sibling_steps > source.node_count || visited[child])
                return ERROR_INVALID_DATA;
            if (source.nodes[child].parent != current)
                return ERROR_INVALID_DATA;
            visited[child] = 1;
            children.push_back(child);
            child = source.nodes[child].next_sibling;
        }
        for (auto it = children.rbegin(); it != children.rend(); ++it)
            stack.push_back(*it);
    }

    NodePool compacted;
    if (compacted.Full()) return ERROR_NOT_ENOUGH_MEMORY;
    std::vector<uint32_t> remap(source.node_count, UINT32_MAX);
    std::vector<uint64_t> compacted_payload;
    compacted_payload.reserve(order.size());

    for (uint32_t old_index : order) {
        uint32_t new_index = compacted.AllocNode();
        if (new_index == UINT32_MAX) return ERROR_NOT_ENOUGH_MEMORY;
        remap[old_index] = new_index;

        const ScanNode& old_node = source.nodes[old_index];
        ScanNode* new_node = compacted.NodeAt(new_index);
        new_node->size = old_node.size;
        new_node->flags = old_node.flags;
        new_node->parent = UINT32_MAX;
        new_node->first_child = UINT32_MAX;
        new_node->next_sibling = UINT32_MAX;
        new_node->name_len = old_node.name_len;
        const wchar_t* old_name = reinterpret_cast<const wchar_t*>(
            reinterpret_cast<const BYTE*>(source.name_buf) + old_node.name_offset);
        new_node->name_offset = compacted.AppendName(old_name, old_node.name_len);
        if (new_node->name_offset == UINT32_MAX) return ERROR_NOT_ENOUGH_MEMORY;
        compacted_payload.push_back(payload[old_index]);
    }

    std::vector<uint32_t> last_child(order.size(), UINT32_MAX);
    for (size_t new_index = 1; new_index < order.size(); ++new_index) {
        uint32_t old_index = order[new_index];
        uint32_t old_parent = source.nodes[old_index].parent;
        if (old_parent >= remap.size() || remap[old_parent] == UINT32_MAX)
            return ERROR_INVALID_DATA;
        uint32_t new_parent = remap[old_parent];
        ScanNode* node = compacted.NodeAt(static_cast<uint32_t>(new_index));
        node->parent = new_parent;
        ScanNode* parent = compacted.NodeAt(new_parent);
        if (parent->first_child == UINT32_MAX)
            parent->first_child = static_cast<uint32_t>(new_index);
        else
            compacted.NodeAt(last_child[new_parent])->next_sibling =
                static_cast<uint32_t>(new_index);
        last_child[new_parent] = static_cast<uint32_t>(new_index);
    }

    pool.Swap(compacted);
    payload.swap(compacted_payload);
    return ERROR_SUCCESS;
}
