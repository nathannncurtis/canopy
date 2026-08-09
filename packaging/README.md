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
