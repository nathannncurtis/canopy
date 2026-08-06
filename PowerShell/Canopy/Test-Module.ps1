# Dependency-free smoke validation; run with: pwsh -NoProfile -File ./Test-Module.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$moduleRoot = Split-Path -Parent $PSCommandPath
$manifestPath = Join-Path $moduleRoot 'Canopy.psd1'
$modulePath = Join-Path $moduleRoot 'Canopy.psm1'

$tokens = $null
$errors = $null
$null = [Management.Automation.Language.Parser]::ParseFile(
    $modulePath, [ref] $tokens, [ref] $errors)
if ($errors.Count -ne 0) {
    throw "Module syntax errors:`n$($errors | Out-String)"
}

$manifest = Test-ModuleManifest -Path $manifestPath
if ($manifest.ExportedFunctions.Keys -notcontains 'Invoke-CanopyScan') {
    throw 'Invoke-CanopyScan is not exported by the manifest.'
}

Import-Module $manifestPath -Force
$command = Get-Command Invoke-CanopyScan -CommandType Function
foreach ($parameter in @('Path', 'CliPath', 'MaximumDepth', 'WorkerThreads',
        'MinimumFileSize', 'MaximumFileSize', 'ExcludedPattern', 'ExcludedExtension')) {
    if (-not $command.Parameters.ContainsKey($parameter)) {
        throw "Expected parameter '$parameter' was not found."
    }
}

'Canopy PowerShell module smoke validation passed.'
