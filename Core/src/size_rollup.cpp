#include "size_rollup.h"
#include "../include/smon_api.h"

void RollupSizes(ScanResult* result)
{
    if (!result || !result->nodes || result->node_count == 0)
        return;

    uint64_t total_bytes = 0;
    uint64_t file_count  = 0;
    uint64_t dir_count   = 0;

    // Tally counts in forward order before rolling up sizes.
    for (uint32_t i = 0; i < result->node_count; ++i) {
        ScanNode* n = &result->nodes[i];
        if (n->flags & SMON_FLAG_DIRECTORY)
            ++dir_count;
        else
            ++file_count;
    }

    // Single reverse pass: parents always have lower indices than children,
    // so processing highest index first ensures children propagate before parents.
    for (uint32_t i = result->node_count; i-- > 0; ) {
        ScanNode* n = &result->nodes[i];
        if (n->parent != UINT32_MAX)
            result->nodes[n->parent].size += n->size;
        else
            total_bytes += n->size; // root contributes to total
    }

    result->total_bytes = total_bytes;
    result->file_count  = file_count;
    result->dir_count   = dir_count;
}
