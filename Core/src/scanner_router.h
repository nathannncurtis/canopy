#pragma once

struct ScanContext;

bool RouterIsNtfs(const wchar_t* path);
bool RouterIsElevated();
bool RouterBeginScan(ScanContext* ctx, const wchar_t* path);
