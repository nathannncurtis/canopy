[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$PortableArchivePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [datetime]$ReleaseDate = (Get-Date).Date
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$installer = (Resolve-Path $InstallerPath).Path
$portable = (Resolve-Path $PortableArchivePath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path (Join-Path $output 'winget') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $output 'scoop') -Force | Out-Null

$tokens = @{
    '__VERSION__' = $Version
    '__RELEASE_DATE__' = $ReleaseDate.ToString('yyyy-MM-dd')
    '__INSTALLER_SHA256__' = (Get-FileHash $installer -Algorithm SHA256).Hash.ToUpperInvariant()
    '__PORTABLE_SHA256__' = (Get-FileHash $portable -Algorithm SHA256).Hash.ToLowerInvariant()
}
foreach ($directory in @('winget', 'scoop')) {
    Get-ChildItem (Join-Path $root $directory) -File | ForEach-Object {
        $content = Get-Content $_.FullName -Raw
        foreach ($token in $tokens.GetEnumerator()) { $content = $content.Replace($token.Key, $token.Value) }
        Set-Content (Join-Path (Join-Path $output $directory) $_.Name) $content -Encoding utf8NoBOM
    }
}
& (Join-Path $PSScriptRoot 'Test-Distribution.ps1') -ManifestRoot $output
if ($LASTEXITCODE -ne 0) { throw 'Generated manifest validation failed.' }
Write-Host "Generated validated manifests in $output"
