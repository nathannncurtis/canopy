#pragma once
#include "node_pool.h"
#include <vector>

// Replaces pool with a parent-before-child copy of root and its descendants.
// payload is remapped in parallel with nodes. Returns ERROR_SUCCESS or a Win32 error.
DWORD CompactSubtree(NodePool& pool, uint32_t root, std::vector<uint64_t>& payload);
