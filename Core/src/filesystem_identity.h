#pragma once
#include <windows.h>
#include <stdint.h>
#include <unordered_map>
#include <unordered_set>
#include "../include/smon_api.h"

class FileAllocationTracker {
public:
    bool Account(DWORD volume_serial, uint64_t file_id);
private:
    std::unordered_map<DWORD, std::unordered_set<uint64_t>> m_seen;
};

SmonNodeMetadata BuildMftNodeMetadata(DWORD volume_serial, uint64_t file_reference,
    const FILE_STANDARD_INFO& standard_info, bool directory, DWORD file_attributes = 0,
    uint64_t last_write_filetime = 0);
