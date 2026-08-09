[CmdletBinding()]
param(
    [string]$ManifestRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$TemplateMode
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ManifestRoot)
$errors = [Collections.Generic.List[string]]::new()
$winget = Join-Path $root 'winget'
$scoop = Join-Path $root 'scoop\canopy.json'
$required = @(
    (Join-Path $winget 'NathanCurtis.Canopy.yaml'),
    (Join-Path $winget 'NathanCurtis.Canopy.installer.yaml'),
    (Join-Path $winget 'NathanCurtis.Canopy.locale.en-US.yaml'),
    $scoop
)
if ($TemplateMode) { $required += (Join-Path $root 'msix\Package.appxmanifest') }
foreach ($path in $required) { if (-not (Test-Path $path)) { $errors.Add("Missing $path") } }
if ($errors.Count -eq 0) {
    if ($TemplateMode) {
        try { [xml](Get-Content (Join-Path $root 'msix\Package.appxmanifest') -Raw) | Out-Null }
        catch { $errors.Add("MSIX manifest XML is invalid: $($_.Exception.Message)") }
    }
    try { $scoopDocument = Get-Content $scoop -Raw | ConvertFrom-Json -Depth 20 }
    catch { $errors.Add("Scoop manifest JSON is invalid: $($_.Exception.Message)") }
    if ($scoopDocument.architecture.'64bit'.url -notmatch 'Canopy-v') { $errors.Add('Scoop x64 URL is missing.') }
    foreach ($file in Get-ChildItem $winget -File) {
        $text = Get-Content $file.FullName -Raw
        foreach ($field in @('PackageIdentifier:', 'PackageVersion:', 'ManifestType:', 'ManifestVersion:')) {
            if (-not $text.Contains($field)) { $errors.Add("$($file.Name) lacks $field") }
        }
    }
    if (-not $TemplateMode) {
        $all = (Get-ChildItem $root -File -Recurse | Get-Content -Raw) -join "`n"
        if ($all -match '__[A-Z0-9_]+__') { $errors.Add('Generated output still contains replacement tokens.') }
        $installerText = Get-Content (Join-Path $winget 'NathanCurtis.Canopy.installer.yaml') -Raw
        if ($installerText -notmatch 'InstallerSha256: [A-F0-9]{64}') { $errors.Add('WinGet installer hash is invalid.') }
        if ($scoopDocument.architecture.'64bit'.hash -notmatch '^[a-f0-9]{64}$') { $errors.Add('Scoop archive hash is invalid.') }
    }
}
if ($errors.Count) { $errors | ForEach-Object { Write-Error $_ }; exit 1 }

$wingetCommand = Get-Command winget -ErrorAction SilentlyContinue
if ($wingetCommand -and -not $TemplateMode) {
    & $wingetCommand.Source validate --manifest $winget
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else { Write-Host 'Structural validation passed (winget CLI validation unavailable or template mode).' }
