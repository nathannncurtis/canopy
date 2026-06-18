# Size Monitor

![C++](https://img.shields.io/badge/C%2B%2B-20-blue)
![C#](https://img.shields.io/badge/C%23-.NET%209-purple)
![License](https://img.shields.io/badge/license-GPL%203.0-blue)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![Version](https://img.shields.io/badge/version-1.0.0-orange)

**A fast Windows disk space analyzer with a native WPF interface.**

---

## Why Size Monitor?

Most disk analyzers either scan slowly or feel dated. Size Monitor uses the NTFS Master File Table directly — the same index Windows already maintains — to enumerate every file on a volume in a single pass without opening any files. On large drives this is dramatically faster than walking the directory tree the conventional way.

- MFT enumeration via USN journal (`FSCTL_ENUM_USN_DATA`) for local NTFS volumes
- `NtQueryDirectoryFile` with a thread pool for UNC paths (`\\server\share`)
- AVX2 SIMD size accumulation with runtime CPU dispatch and scalar fallback
- Squarified treemap with drill-down navigation (left-click in, right-click out)
- Directory tree view with proportional size bars
- Fluent Design UI (WPF-UI, Mica backdrop, dark theme)

## Requirements

- Windows 10 or later
- Administrator privileges for MFT scanning (falls back to directory scan without elevation)

## Usage

Download the latest release ZIP, extract it, and run `SizeMonitor.exe`. The DLL (`SizeMonitor.Core.dll`) must stay alongside the EXE.

Enter a local path (`C:\`) or a UNC path (`\\server\share`) and click **Scan**.

## Building from Source

**Prerequisites:** Visual Studio 2022 (C++ workload), CMake 3.25+, .NET 9 SDK.

```bat
build.bat
```

Output lands in `dist\Size Monitor\`. The build is unsigned.

Or build components separately:

```bat
cmake -S Core -B Core\build -A x64
cmake --build Core\build --config Release
dotnet publish App\SizeMonitor.App.csproj -c Release -r win-x64 --self-contained true -o dist\out
copy Core\build\bin\Release\SizeMonitor.Core.dll dist\out\
```

## License

GPL-3.0. See [LICENSE](LICENSE).
