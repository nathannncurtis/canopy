#include "filesystem_identity.h"

bool FileAllocationTracker::Account(DWORD volume_serial, uint64_t file_id)
{
    return m_seen[volume_serial].insert(file_id).second;
}

SmonNodeMetadata BuildMftNodeMetadata(DWORD volume_serial, uint64_t file_reference,
    const FILE_STANDARD_INFO& info, bool directory, DWORD file_attributes,
    uint64_t last_write_filetime)
{
    SmonNodeMetadata metadata{};
    metadata.struct_size = sizeof(metadata);
    metadata.flags = SMON_NODE_META_UNIQUE_ALLOCATION;
    if (info.NumberOfLinks > 1) metadata.flags |= SMON_NODE_META_CANONICAL_LINK_ONLY;
    if (file_attributes & FILE_ATTRIBUTE_COMPRESSED) metadata.flags |= SMON_NODE_META_COMPRESSED;
    if (file_attributes & FILE_ATTRIBUTE_SPARSE_FILE) metadata.flags |= SMON_NODE_META_SPARSE;
    metadata.link_count = info.NumberOfLinks;
    metadata.volume_serial = volume_serial;
    metadata.file_id = file_reference;
    metadata.logical_bytes = static_cast<uint64_t>(info.EndOfFile.QuadPart);
    metadata.allocated_bytes = directory ? 0 : static_cast<uint64_t>(info.AllocationSize.QuadPart);
    metadata.uniquely_accounted_bytes = metadata.allocated_bytes;
    metadata.last_write_filetime = last_write_filetime;
    return metadata;
}
