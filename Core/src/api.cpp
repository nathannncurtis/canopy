#include "../include/smon_api.h"
#include "node_pool.h"

ScanHandle WINAPI Smon_BeginScan(const wchar_t*, SmonProgressCallback, void*)
{
    return nullptr;
}

BOOL WINAPI Smon_Cancel(ScanHandle)
{
    return FALSE;
}

BOOL WINAPI Smon_Wait(ScanHandle, DWORD)
{
    return FALSE;
}

BOOL WINAPI Smon_GetResult(ScanHandle, ScanResult*)
{
    return FALSE;
}

void WINAPI Smon_FreeResult(ScanHandle)
{
}

BOOL WINAPI Smon_IsNtfsVolume(const wchar_t*)
{
    return FALSE;
}
