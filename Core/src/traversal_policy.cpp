#include "traversal_policy.h"

TraversalDecision TraversalIdentityTracker::Observe(
    DWORD volume_serial, uint64_t file_id, bool stay_on_volume)
{
    if (!m_has_root_volume) {
        m_has_root_volume = true;
        m_root_volume_serial = volume_serial;
    } else if (stay_on_volume && volume_serial != m_root_volume_serial) {
        return TraversalDecision::DifferentVolume;
    }
    return m_visited[volume_serial].insert(file_id).second
        ? TraversalDecision::Visit
        : TraversalDecision::AlreadyVisited;
}
