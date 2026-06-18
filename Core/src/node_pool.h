#pragma once
#include "../include/smon_api.h"

// Arena allocator for ScanNode records and the wchar_t name pool.
// Not thread-safe; callers must synchronize.
class NodePool {
public:
    NodePool();
    ~NodePool();

    // Returns index of a new zero-initialized ScanNode; grows committed pages as needed.
    uint32_t AllocNode();

    // Copies len wchar_t characters into the name pool; returns byte offset from name_buf start.
    uint32_t AppendName(const wchar_t* name, uint32_t len);

    ScanNode* NodeAt(uint32_t index);

    // Fills out->nodes, out->node_count, out->name_buf. Does not set totals.
    void Finalize(ScanResult* out);

    bool Full() const;

private:
    void GrowNodes();
    void GrowNames(uint32_t need_bytes);

    static constexpr SIZE_T kReserveTotal   = 256ull * 1024 * 1024; // 256 MB total VA
    static constexpr SIZE_T kHalf           = kReserveTotal / 2;    // 128 MB each region
    static constexpr SIZE_T kCommitChunk    = 4 * 1024 * 1024;      // 4 MB at a time

    BYTE*    m_base        = nullptr;   // start of reservation
    BYTE*    m_name_base   = nullptr;   // m_base + kHalf

    SIZE_T   m_node_committed = 0;      // bytes committed in node region
    SIZE_T   m_name_committed = 0;      // bytes committed in name region

    uint32_t m_node_count  = 0;
    uint32_t m_name_used   = 0;         // bytes used in name region
};
