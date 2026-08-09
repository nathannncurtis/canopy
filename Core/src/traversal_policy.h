#pragma once
#include <windows.h>
#include <stdint.h>
#include <unordered_map>
#include <unordered_set>

enum class TraversalDecision { Visit, AlreadyVisited, DifferentVolume };

class TraversalIdentityTracker {
public:
    TraversalDecision Observe(DWORD volume_serial, uint64_t file_id, bool stay_on_volume);
    bool HasRootVolume() const { return m_has_root_volume; }
    DWORD RootVolumeSerial() const { return m_root_volume_serial; }

private:
    bool m_has_root_volume = false;
    DWORD m_root_volume_serial = 0;
    std::unordered_map<DWORD, std::unordered_set<uint64_t>> m_visited;
};
