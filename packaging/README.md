# Canopy distribution packages

This directory keeps the MSIX, WinGet, and Scoop definitions reviewable beside the source. Release builds replace tokens from actual artifacts; committed manifests intentionally never contain guessed hashes.

## Validate templates

```powershell
./packaging/scripts/Test-Distribution.ps1 -TemplateMode
```

The validator parses the MSIX XML and Scoop JSON, checks the required WinGet fields, and rejects unresolved tokens in generated output. If `winget` is available, generated manifests also run through `winget validate`.

## Build an MSIX

First create the normal self-contained distribution so `Canopy.exe` and `Canopy.Core.dll` are together, then run:

```powershell
./packaging/scripts/Build-Msix.ps1 `
  -InputDirectory ./dist/Canopy `
  -OutputPath ./dist/Canopy-v1.0.0-x64.msix `
  -Version 1.0.0
```

The script discovers `makeappx.exe` from the Windows SDK, stages the payload in a unique temporary directory, normalizes the version to four components, and creates architecture-aware package identity. It creates deterministic placeholder tiles suitable for development; replace them with final branded PNG assets before Store submission.

Pass `-Architecture arm64` for a native ARM64 payload. ARM64 packages must contain
the `win-arm64` managed apphost and an ARM64 `Canopy.Core.dll`; x64 binaries or x64
emulation are not accepted as substitutes.

## ARM64 and lightweight framework-dependent artifacts

The architecture build script compiles and tests the native core on the selected
architecture, publishes the matching WPF apphost, and keeps the native DLL beside it:

```powershell
./packaging/scripts/Build-ArchitectureArtifacts.ps1 `
  -Architecture arm64 -Version 1.0.0 -CompareSelfContained
```

Supported values are `x64` and `arm64`. ARM64 excludes the x64 MASM object entirely
and uses the native ARM64 intrinsic/scalar dispatch. The framework-dependent ZIP is
checked to exclude `coreclr.dll` and to be smaller than its self-contained comparison.
It requires the matching **.NET Desktop Runtime 9** architecture. `Canopy.exe` uses
the standard .NET apphost missing-framework dialog, and `RUNTIME-REQUIRED.txt` gives
the direct prerequisite and download location before launch.

CI runs the ABI, filesystem corpus, architecture-dispatch parity, managed contracts,
and artifact-size checks on native x64 and native Windows ARM64 runners. A local x64
machine without the Visual Studio ARM64 C++ tools cannot execute that hardware gate;
installing those tools permits cross-compilation, but representative scans and intrinsic
dispatch still require the ARM64 CI runner or physical ARM64 Windows hardware.

For a locally trusted package, pass `-PfxPath` and `-PfxPassword`. The certificate subject must exactly match `-Publisher`. Never commit a PFX or password. CI should inject signing material from an approved secret store and delete it after signing.

`Canopy.Package.wapproj` is included for Visual Studio's Windows Application Packaging Project tooling. The script is the headless build path and ensures the native DLL remains beside the executable, avoiding loader failures.

## Generate repository manifests

After the Inno installer and portable ZIP have been built:

```powershell
./packaging/scripts/New-ReleaseManifests.ps1 `
  -Version 1.0.0 `
  -InstallerPath ./dist/Canopy-v1.0.0-setup.exe `
  -PortableArchivePath ./Canopy-v1.0.0.zip `
  -OutputDirectory ./dist/manifests
```

The script computes SHA-256 values from those exact files, substitutes version/date/hash tokens, and validates the result. It never downloads artifacts and never publishes anything.

## External publication steps

These require accounts and are deliberately not automated here:

1. Sign the MSIX with the Store-assigned publisher identity or a publicly trusted code-signing certificate. Reserve the package identity in Partner Center before Store submission.
2. Fork `microsoft/winget-pkgs`, copy the generated three-file WinGet manifest into the repository's required package/version path, run `winget validate`, and submit a pull request.
3. Submit the generated `canopy.json` to a Scoop bucket you control (or request inclusion in an established bucket) after the GitHub release URL is live.

WinGet and Scoop review the public artifact independently. Do not submit manifests until the release URLs exist and their computed hashes match the immutable uploaded files.
