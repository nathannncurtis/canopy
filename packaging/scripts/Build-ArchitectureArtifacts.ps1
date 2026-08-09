[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')][string]$Version,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) '..\dist'),
    [switch]$CompareSelfContained
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$rid = "win-$Architecture"
$cmakePlatform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$nativeBuild = Join-Path $repository "Core\build-$Architecture-distribution"
$frameworkStage = Join-Path $output "Canopy-$Version-$Architecture-framework-dependent"

New-Item -ItemType Directory -Path $output -Force | Out-Null
& cmake -S (Join-Path $repository 'Core') -B $nativeBuild -A $cmakePlatform -DSMON_ENABLE_AVX2_SUM=OFF
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed for $Architecture." }
& cmake --build $nativeBuild --config Release
if ($LASTEXITCODE -ne 0) { throw "Native build failed for $Architecture." }
& ctest --test-dir $nativeBuild -C Release --output-on-failure
if ($LASTEXITCODE -ne 0) { throw "Native tests failed for $Architecture." }

if (Test-Path $frameworkStage) { Remove-Item $frameworkStage -Recurse -Force }
& dotnet publish (Join-Path $repository 'App\SizeMonitor.App.csproj') -c Release -r $rid `
    --self-contained false -p:UseAppHost=true -p:Version=$Version -o $frameworkStage
if ($LASTEXITCODE -ne 0) { throw "Framework-dependent publish failed for $rid." }
Copy-Item (Join-Path $nativeBuild 'bin\Release\Canopy.Core.dll') $frameworkStage -Force
Copy-Item (Join-Path $repository 'version.txt') $frameworkStage -Force

$guidance = @"
Canopy $Version for Windows $Architecture (framework-dependent)

Prerequisite: Microsoft .NET Desktop Runtime 9 for $Architecture.
Install it from https://dotnet.microsoft.com/download/dotnet/9.0 before running Canopy.exe.

Canopy.exe is a native $Architecture apphost. If the compatible runtime is absent, the
.NET host reports the missing Microsoft.WindowsDesktop.App framework and offers the
runtime download. Canopy.Core.dll must remain beside Canopy.exe.
"@
Set-Content (Join-Path $frameworkStage 'RUNTIME-REQUIRED.txt') $guidance -Encoding utf8NoBOM

$runtimeConfig = Join-Path $frameworkStage 'Canopy.runtimeconfig.json'
if (-not (Test-Path $runtimeConfig)) { throw 'Framework-dependent output lacks Canopy.runtimeconfig.json.' }
if (Test-Path (Join-Path $frameworkStage 'coreclr.dll')) { throw 'Framework-dependent output unexpectedly contains coreclr.dll.' }
$runtimeText = Get-Content $runtimeConfig -Raw
if ($runtimeText -notmatch 'Microsoft.WindowsDesktop.App' -or $runtimeText -notmatch '9\.0') {
    throw 'Runtime configuration does not declare the .NET 9 Windows Desktop prerequisite.'
}

$frameworkZip = Join-Path $output "Canopy-$Version-$Architecture-framework-dependent.zip"
if (Test-Path $frameworkZip) { Remove-Item $frameworkZip -Force }
Compress-Archive (Join-Path $frameworkStage '*') $frameworkZip -CompressionLevel Optimal
$frameworkSize = (Get-Item $frameworkZip).Length
$selfContainedSize = $null

if ($CompareSelfContained) {
    $selfContainedStage = Join-Path $output "Canopy-$Version-$Architecture-self-contained"
    if (Test-Path $selfContainedStage) { Remove-Item $selfContainedStage -Recurse -Force }
    & dotnet publish (Join-Path $repository 'App\SizeMonitor.App.csproj') -c Release -r $rid `
        --self-contained true -p:Version=$Version -o $selfContainedStage
    if ($LASTEXITCODE -ne 0) { throw "Self-contained comparison publish failed for $rid." }
    Copy-Item (Join-Path $nativeBuild 'bin\Release\Canopy.Core.dll') $selfContainedStage -Force
    $selfContainedZip = Join-Path $output "Canopy-$Version-$Architecture-self-contained.zip"
    if (Test-Path $selfContainedZip) { Remove-Item $selfContainedZip -Force }
    Compress-Archive (Join-Path $selfContainedStage '*') $selfContainedZip -CompressionLevel Optimal
    $selfContainedSize = (Get-Item $selfContainedZip).Length
    if ($frameworkSize -ge $selfContainedSize) {
        throw "Framework-dependent artifact ($frameworkSize bytes) is not smaller than self-contained ($selfContainedSize bytes)."
    }
}

[ordered]@{
    version = $Version
    architecture = $Architecture
    runtimeIdentifier = $rid
    frameworkDependentZip = $frameworkZip
    frameworkDependentBytes = $frameworkSize
    selfContainedBytes = $selfContainedSize
} | ConvertTo-Json | Set-Content (Join-Path $output "Canopy-$Version-$Architecture-artifacts.json") -Encoding utf8NoBOM

Write-Host "Created native $Architecture framework-dependent artifact: $frameworkZip ($frameworkSize bytes)."
