#include "node_pool.h"
#include <cstring>

static_assert(sizeof(ScanNode) == 32, "ScanNode must be 32 bytes");

NodePool::NodePool()
{
    m_base = static_cast<BYTE*>(VirtualAlloc(nullptr, kReserveTotal,
                                              MEM_RESERVE, PAGE_READWRITE));
    if (!m_base)
        return;
    m_name_base = m_base + kHalf;

    // Commit an initial chunk in each region so the first alloc never stalls.
    VirtualAlloc(m_base,      kCommitChunk, MEM_COMMIT, PAGE_READWRITE);
    VirtualAlloc(m_name_base, kCommitChunk, MEM_COMMIT, PAGE_READWRITE);
    m_node_committed = kCommitChunk;
    m_name_committed = kCommitChunk;
}

NodePool::~NodePool()
{
    if (m_base)
        VirtualFree(m_base, 0, MEM_RELEASE);
}

uint32_t NodePool::AllocNode()
{
    SIZE_T needed = (static_cast<SIZE_T>(m_node_count) + 1) * sizeof(ScanNode);
    if (needed > m_node_committed)
        GrowNodes();

    uint32_t idx = m_node_count++;
    ScanNode* n  = NodeAt(idx);
    memset(n, 0, sizeof(ScanNode));
    return idx;
}

uint32_t NodePool::AppendName(const wchar_t* name, uint32_t len)
{
    uint32_t byte_len = len * static_cast<uint32_t>(sizeof(wchar_t));
    uint32_t offset   = m_name_used;

    SIZE_T needed = static_cast<SIZE_T>(m_name_used) + byte_len;
    if (needed > m_name_committed)
        GrowNames(byte_len);

    memcpy(m_name_base + m_name_used, name, byte_len);
    m_name_used += byte_len;
    return offset;
}

ScanNode* NodePool::NodeAt(uint32_t index)
{
    return reinterpret_cast<ScanNode*>(m_base) + index;
}

void NodePool::Finalize(ScanResult* out)
{
    out->nodes      = reinterpret_cast<ScanNode*>(m_base);
    out->node_count = m_node_count;
    out->name_buf   = reinterpret_cast<wchar_t*>(m_name_base);
}

bool NodePool::Full() const
{
    // Safety cap: refuse before we hit UINT32_MAX index space.
    return m_node_count >= (UINT32_MAX / 2);
}

void NodePool::GrowNodes()
{
    BYTE* commit_at = m_base + m_node_committed;
    SIZE_T remaining = kHalf - m_node_committed;
    SIZE_T chunk = remaining < kCommitChunk ? remaining : kCommitChunk;
    if (chunk == 0)
        return; // reservation exhausted
    VirtualAlloc(commit_at, chunk, MEM_COMMIT, PAGE_READWRITE);
    m_node_committed += chunk;
}

void NodePool::GrowNames(uint32_t need_bytes)
{
    SIZE_T target = static_cast<SIZE_T>(m_name_used) + need_bytes;
    while (m_name_committed < target) {
        BYTE*  commit_at  = m_name_base + m_name_committed;
        SIZE_T remaining  = kHalf - m_name_committed;
        SIZE_T chunk      = remaining < kCommitChunk ? remaining : kCommitChunk;
        if (chunk == 0)
            break; // reservation exhausted
        VirtualAlloc(commit_at, chunk, MEM_COMMIT, PAGE_READWRITE);
        m_name_committed += chunk;
    }
}
