# canopy

![C++](https://img.shields.io/badge/C%2B%2B-20-blue)
![C#](https://img.shields.io/badge/C%23-.NET%209-purple)
![License](https://img.shields.io/badge/license-GPL%203.0-blue)
![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%7C%20ARM64-lightgrey)
![Version](https://img.shields.io/badge/version-1.0.0-orange)

**A fast Windows disk space analyzer with a native WPF interface.**

---

## Why Canopy?

Most disk analyzers either scan slowly or feel dated. Canopy uses the NTFS Master File Table directly (the same index Windows already maintains) to enumerate every file on a volume in a single pass without opening any files. On large drives this is dramatically faster than walking the directory tree the conventional way.

- MFT enumeration via USN journal (`FSCTL_ENUM_USN_DATA`) for local NTFS volumes
- `NtQueryDirectoryFile` with a thread pool for UNC paths (`\\server\share`)
- Architecture-dispatched accumulation: measured x64 AVX2 assembly when enabled,
  ARM64 intrinsics on native ARM64, and a portable scalar fallback
- Squarified treemap with drill-down navigation (left-click in, right-click out)
- Directory tree view with proportional size bars
- Fluent Design UI (WPF-UI, Mica backdrop, dark theme)

## Requirements

- Windows 10 or later
- x64 or ARM64 processor; ARM64 packages run natively without x64 emulation
- Administrator privileges for MFT scanning (falls back to directory scan without elevation)

## Usage

Download the latest release ZIP, extract it, and run `Canopy.exe`. The DLL (`Canopy.Core.dll`) must stay alongside the EXE.

Enter a local path (`C:\`) or a UNC path (`\\server\share`) and click **Scan**.

## Building from Source

**Prerequisites:** Visual Studio 2022 (C++ workload), CMake 3.25+, .NET 9 SDK.

```bat
build.bat
build.bat arm64
```

Output lands in `dist\Canopy\`. The build is unsigned.

MSIX packaging plus WinGet and Scoop release manifests are documented in
[`packaging/README.md`](packaging/README.md). Package templates are validated in CI, while
signing and external catalog submission remain explicit release-owner steps.

Or build components separately:

```bat
cmake -S Core -B Core\build -A x64
cmake --build Core\build --config Release
dotnet publish App\SizeMonitor.App.csproj -c Release -r win-x64 --self-contained true -o dist\Canopy
copy Core\build\bin\Release\Canopy.Core.dll dist\Canopy\
```

For native ARM64, use `-A ARM64`, `-r win-arm64`, and the ARM64 Visual Studio C++
tools. Lightweight framework-dependent ZIPs for both architectures can be created
with `packaging/scripts/Build-ArchitectureArtifacts.ps1`; they require the matching
.NET Desktop Runtime 9 and include explicit prerequisite guidance. Native ARM64 corpus
execution is gated in CI because it cannot be verified through x64 emulation.

## License

GPL-3.0. See [LICENSE](LICENSE).
