#include "size_rollup.h"
#include "cpu_features.h"
#include "architecture_dispatch.h"
#include "../include/smon_api.h"
#include "../asm/simd_sum.h"

void RollupSizes(ScanResult* result)
{
    if (!result || !result->nodes || result->node_count == 0)
        return;

    uint64_t file_count = 0;
    uint64_t dir_count  = 0;

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
    }

    // After propagation, sum sizes of root-level nodes to get total_bytes.
    // In nearly all scans there is exactly one root, but handle the general case.
    // Use SIMD when available; a small scratch array avoids a second indirection loop.
    uint64_t total_bytes = 0;
    {
        // Count roots first so we can decide whether SIMD is worth it.
        uint32_t root_count = 0;
        for (uint32_t i = 0; i < result->node_count; ++i) {
            if (result->nodes[i].parent == UINT32_MAX)
                ++root_count;
        }

#if defined(SMON_ENABLE_AVX2_SUM) || defined(_M_ARM64) || defined(__aarch64__)
        if (SmonGetSumPath() != SmonSumPath::Scalar && root_count > 1) {
            // Gather root sizes into a contiguous array for the SIMD sum.
            uint64_t* scratch = new uint64_t[root_count];
            uint32_t  idx     = 0;
            for (uint32_t i = 0; i < result->node_count; ++i) {
                if (result->nodes[i].parent == UINT32_MAX)
                    scratch[idx++] = result->nodes[i].size;
            }
            total_bytes = SmonSumU64_Architecture(scratch, root_count);
            delete[] scratch;
        } else
#endif
        {
            // Scalar path: single root (common case) or no AVX2.
            for (uint32_t i = 0; i < result->node_count; ++i) {
                if (result->nodes[i].parent == UINT32_MAX)
                    total_bytes += result->nodes[i].size;
            }
        }
    }

    result->total_bytes = total_bytes;
    result->file_count  = file_count;
    result->dir_count   = dir_count;
}
