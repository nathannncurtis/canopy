[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')][string]$Version,
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [string]$Publisher = 'CN=Nathan Curtis',
    [string]$PfxPath,
    [string]$PfxPassword
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = (Resolve-Path $InputDirectory).Path
if (-not (Test-Path (Join-Path $source 'Canopy.exe'))) { throw 'InputDirectory must contain Canopy.exe.' }
if (-not (Test-Path (Join-Path $source 'Canopy.Core.dll'))) { throw 'InputDirectory must contain Canopy.Core.dll.' }

$parts = @($Version.Split('.') | ForEach-Object { [uint16]$_ })
while ($parts.Count -lt 4) { $parts += 0 }
$packageVersion = $parts -join '.'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('canopy-msix-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item (Join-Path $source '*') $stage -Recurse -Force
    $manifestPath = Join-Path $stage 'AppxManifest.xml'
    Copy-Item (Join-Path $root 'msix\Package.appxmanifest') $manifestPath
    [xml]$manifest = Get-Content $manifestPath -Raw
    $manifest.Package.Identity.Version = $packageVersion
    $manifest.Package.Identity.ProcessorArchitecture = $Architecture
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Save($manifestPath)

    Add-Type -AssemblyName System.Drawing
    $assetDir = Join-Path $stage 'Assets'
    New-Item -ItemType Directory -Path $assetDir | Out-Null
    $assets = @{ 'StoreLogo.png' = @(50,50); 'Square44x44Logo.png' = @(44,44); 'Square150x150Logo.png' = @(150,150); 'Wide310x150Logo.png' = @(310,150) }
    foreach ($asset in $assets.GetEnumerator()) {
        $bitmap = [Drawing.Bitmap]::new($asset.Value[0], $asset.Value[1])
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try { $graphics.Clear([Drawing.Color]::FromArgb(31, 95, 74)) }
            finally { $graphics.Dispose() }
            $bitmap.Save((Join-Path $assetDir $asset.Key), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $bitmap.Dispose() }
    }

    $makeAppx = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if (-not $makeAppx) {
        $makeAppx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
    }
    if (-not $makeAppx) { throw 'makeappx.exe was not found. Install the Windows 10/11 SDK.' }
    $output = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
    & $makeAppx.FullName pack /d $stage /p $output /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE." }

    if ($PfxPath) {
        $signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signTool) { throw 'signtool.exe was not found.' }
        & $signTool.FullName sign /fd SHA256 /f $PfxPath /p $PfxPassword $output
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }
    }
    Write-Host "Created $output"
} finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
