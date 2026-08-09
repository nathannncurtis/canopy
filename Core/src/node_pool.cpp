#include "node_pool.h"
#include <cstring>
#include <utility>

static_assert(sizeof(ScanNode) == 32, "ScanNode must be 32 bytes");

NodePool::NodePool()
{
    m_base = static_cast<BYTE*>(VirtualAlloc(nullptr, kReserveTotal,
                                              MEM_RESERVE, PAGE_READWRITE));
    if (!m_base)
        return;
    m_name_base = m_base + kHalf;

    // Commit an initial chunk in each region so the first alloc never stalls.
    void* nodes = VirtualAlloc(m_base, kCommitChunk, MEM_COMMIT, PAGE_READWRITE);
    void* names = VirtualAlloc(m_name_base, kCommitChunk, MEM_COMMIT, PAGE_READWRITE);
    if (!nodes || !names) {
        VirtualFree(m_base, 0, MEM_RELEASE);
        m_base = nullptr;
        m_name_base = nullptr;
        return;
    }
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
    if (Full()) return UINT32_MAX;

    SIZE_T needed = (static_cast<SIZE_T>(m_node_count) + 1) * sizeof(ScanNode);
    if (needed > m_node_committed && !GrowNodes()) return UINT32_MAX;

    uint32_t idx = m_node_count++;
    ScanNode* n  = NodeAt(idx);
    memset(n, 0, sizeof(ScanNode));
    return idx;
}

uint32_t NodePool::AppendName(const wchar_t* name, uint32_t len)
{
    if (!m_base || (len > 0 && !name) ||
        len > UINT32_MAX / static_cast<uint32_t>(sizeof(wchar_t)))
        return UINT32_MAX;

    uint32_t byte_len = len * static_cast<uint32_t>(sizeof(wchar_t));
    uint32_t offset   = m_name_used;

    SIZE_T needed = static_cast<SIZE_T>(m_name_used) + byte_len;
    if (needed > kHalf || (needed > m_name_committed && !GrowNames(byte_len)))
        return UINT32_MAX;

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
    return !m_base || static_cast<SIZE_T>(m_node_count) >= kNodeCapacity;
}

uint32_t NodePool::AllocNamedNode(const wchar_t* name, uint32_t len)
{
    if (!m_base || (len > 0 && !name) || Full() ||
        len > UINT32_MAX / static_cast<uint32_t>(sizeof(wchar_t))) return UINT32_MAX;
    uint32_t byte_len = len * static_cast<uint32_t>(sizeof(wchar_t));
    SIZE_T node_need = (static_cast<SIZE_T>(m_node_count) + 1) * sizeof(ScanNode);
    SIZE_T name_need = static_cast<SIZE_T>(m_name_used) + byte_len;
    if (name_need > kHalf ||
        (node_need > m_node_committed && !GrowNodes()) ||
        (name_need > m_name_committed && !GrowNames(byte_len))) return UINT32_MAX;

    uint32_t index = m_node_count++;
    ScanNode* node = NodeAt(index);
    memset(node, 0, sizeof(*node));
    node->name_offset = m_name_used;
    memcpy(m_name_base + m_name_used, name, byte_len);
    m_name_used += byte_len;
    return index;
}

void NodePool::Swap(NodePool& other) noexcept
{
    using std::swap;
    swap(m_base, other.m_base);
    swap(m_name_base, other.m_name_base);
    swap(m_node_committed, other.m_node_committed);
    swap(m_name_committed, other.m_name_committed);
    swap(m_node_count, other.m_node_count);
    swap(m_name_used, other.m_name_used);
}

bool NodePool::GrowNodes()
{
    BYTE* commit_at = m_base + m_node_committed;
    SIZE_T remaining = kHalf - m_node_committed;
    SIZE_T chunk = remaining < kCommitChunk ? remaining : kCommitChunk;
    if (chunk == 0) return false;
    if (!VirtualAlloc(commit_at, chunk, MEM_COMMIT, PAGE_READWRITE)) return false;
    m_node_committed += chunk;
    return true;
}

bool NodePool::GrowNames(uint32_t need_bytes)
{
    SIZE_T target = static_cast<SIZE_T>(m_name_used) + need_bytes;
    if (target > kHalf) return false;
    while (m_name_committed < target) {
        BYTE*  commit_at  = m_name_base + m_name_committed;
        SIZE_T remaining  = kHalf - m_name_committed;
        SIZE_T chunk      = remaining < kCommitChunk ? remaining : kCommitChunk;
        if (chunk == 0) return false;
        if (!VirtualAlloc(commit_at, chunk, MEM_COMMIT, PAGE_READWRITE)) return false;
        m_name_committed += chunk;
    }
    return true;
}
