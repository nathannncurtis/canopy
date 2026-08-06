#pragma once
#include "../include/smon_api.h"

// Arena allocator for ScanNode records and the wchar_t name pool.
// Not thread-safe; callers must synchronize.
class NodePool {
public:
    static constexpr uint32_t MaxNodes =
        (128u * 1024u * 1024u) / static_cast<uint32_t>(sizeof(ScanNode));
    static constexpr uint32_t MaxNameBytes = 128u * 1024u * 1024u;

    NodePool();
    ~NodePool();
    NodePool(const NodePool&) = delete;
    NodePool& operator=(const NodePool&) = delete;

    // Returns index of a new zero-initialized ScanNode; grows committed pages as needed.
    uint32_t AllocNode();

    // Copies len wchar_t characters into the name pool; returns byte offset from name_buf start.
    uint32_t AppendName(const wchar_t* name, uint32_t len);

    ScanNode* NodeAt(uint32_t index);

    // Fills out->nodes, out->node_count, out->name_buf. Does not set totals.
    void Finalize(ScanResult* out);

    bool Full() const;
    void Swap(NodePool& other) noexcept;

#ifdef SMON_NODE_POOL_TESTING
    void TestSetUsage(uint32_t node_count, uint32_t name_bytes) {
        m_node_count = node_count;
        m_name_used = name_bytes;
    }
#endif

private:
    bool GrowNodes();
    bool GrowNames(uint32_t need_bytes);

    static constexpr SIZE_T kReserveTotal   = 256ull * 1024 * 1024; // 256 MB total VA
    static constexpr SIZE_T kHalf           = kReserveTotal / 2;    // 128 MB each region
    static constexpr SIZE_T kCommitChunk    = 4 * 1024 * 1024;      // 4 MB at a time
    static constexpr SIZE_T kNodeCapacity   = MaxNodes;

    BYTE*    m_base        = nullptr;   // start of reservation
    BYTE*    m_name_base   = nullptr;   // m_base + kHalf

    SIZE_T   m_node_committed = 0;      // bytes committed in node region
    SIZE_T   m_name_committed = 0;      // bytes committed in name region

    uint32_t m_node_count  = 0;
    uint32_t m_name_used   = 0;         // bytes used in name region
};
