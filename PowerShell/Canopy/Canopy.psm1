Set-StrictMode -Version Latest

function Resolve-CanopyCli {
    [CmdletBinding()]
    param([string] $CliPath)

    if (-not [string]::IsNullOrWhiteSpace($CliPath)) {
        $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CliPath)
        if (-not [IO.File]::Exists($resolved)) {
            throw [IO.FileNotFoundException]::new("The Canopy CLI was not found at '$resolved'.", $resolved)
        }
        return $resolved
    }

    if (-not [string]::IsNullOrWhiteSpace($env:CANOPY_CLI_PATH)) {
        return Resolve-CanopyCli -CliPath $env:CANOPY_CLI_PATH
    }

    foreach ($name in @('canopy-cli', 'SizeMonitor.Cli', 'canopy')) {
        $command = Get-Command $name -CommandType Application -ErrorAction Ignore | Select-Object -First 1
        if ($null -ne $command) { return $command.Source }
    }

    throw [IO.FileNotFoundException]::new(
        'Canopy CLI not found. Pass -CliPath, set CANOPY_CLI_PATH, or add canopy to PATH.')
}

function New-CanopyErrorRecord {
    param(
        [int] $ExitCode,
        [string] $Message,
        [string] $Executable
    )

    $mapping = switch ($ExitCode) {
        2 { @('Canopy.InvalidArguments', [Management.Automation.ErrorCategory]::InvalidArgument) }
        3 { @('Canopy.PartialScan', [Management.Automation.ErrorCategory]::OperationStopped) }
        4 { @('Canopy.AllTargetsFailed', [Management.Automation.ErrorCategory]::OperationStopped) }
        130 { @('Canopy.ScanCancelled', [Management.Automation.ErrorCategory]::OperationStopped) }
        default { @('Canopy.CliFailed', [Management.Automation.ErrorCategory]::NotSpecified) }
    }
    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = "Canopy CLI exited with code $ExitCode."
    }
    $exception = [InvalidOperationException]::new($Message)
    return [Management.Automation.ErrorRecord]::new(
        $exception, $mapping[0], $mapping[1], $Executable)
}

function Invoke-CanopyScan {
    <#
    .SYNOPSIS
    Scans one or more paths with the Canopy command-line scanner.

    .DESCRIPTION
    Invokes `canopy-cli` in JSON mode and converts its UTF-8 JSON
    response into PowerShell objects. Paths can be supplied directly or through the
    pipeline. The command captures stderr and maps documented CLI exit codes to
    terminating PowerShell ErrorRecords.

    The eventual CLI contract expected by this module is:
      canopy-cli --json --path <path> [--path <path> ...]

    .PARAMETER Path
    One or more scan targets. Accepts strings and objects with Path or FullName through
    the pipeline.

    .PARAMETER CliPath
    Explicit CLI executable. Otherwise CANOPY_CLI_PATH and then PATH are searched.

    .PARAMETER MaximumDepth
    Maximum scan depth. Omit for an unlimited scan.

    .PARAMETER WorkerThreads
    Directory scanner worker count from 1 through 32.

    .PARAMETER MinimumFileSize
    Minimum logical file size in bytes.

    .PARAMETER MaximumFileSize
    Maximum logical file size in bytes. Omit for no maximum.

    .EXAMPLE
    Invoke-CanopyScan C:\Data -ExcludeHidden -ExcludedExtension .tmp,.log

    .EXAMPLE
    Get-Item C:\Data,D:\Archive | Invoke-CanopyScan -WorkerThreads 8

    .OUTPUTS
    PSCustomObject. JSON objects emitted by the Canopy CLI.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [Alias('FullName')]
        [ValidateNotNullOrEmpty()]
        [string[]] $Path,

        [string] $CliPath,

        [ValidateRange(1, 4294967295)]
        [uint32] $MaximumDepth,

        [ValidateRange(1, 32)]
        [uint32] $WorkerThreads,

        [uint64] $MinimumFileSize,
        [Nullable[uint64]] $MaximumFileSize,
        [switch] $ExcludeHidden,
        [switch] $ExcludeSystem,
        [switch] $ExcludeTemporary,
        [switch] $ExcludeReparsePoints,
        [switch] $ForceDirectoryScanner,
        [string[]] $ExcludedPattern,
        [string[]] $ExcludedExtension
    )

    begin {
        $targets = [Collections.Generic.List[string]]::new()
    }

    process {
        foreach ($item in $Path) {
            if (-not [string]::IsNullOrWhiteSpace($item)) { $targets.Add($item.Trim()) }
        }
    }

    end {
        if ($targets.Count -eq 0) {
            $PSCmdlet.ThrowTerminatingError([Management.Automation.ErrorRecord]::new(
                [ArgumentException]::new('At least one scan path is required.'),
                'Canopy.NoPaths', [Management.Automation.ErrorCategory]::InvalidArgument, $null))
        }
        if ($PSBoundParameters.ContainsKey('MaximumFileSize') -and
            $MaximumFileSize -lt $MinimumFileSize) {
            throw [ArgumentException]::new('MaximumFileSize cannot be less than MinimumFileSize.')
        }

        $executable = Resolve-CanopyCli -CliPath $CliPath
        $arguments = [Collections.Generic.List[string]]::new()
        $arguments.Add('--json')
        foreach ($target in $targets) { $arguments.Add('--path'); $arguments.Add($target) }
        if ($PSBoundParameters.ContainsKey('MaximumDepth')) {
            $arguments.Add('--max-depth'); $arguments.Add($MaximumDepth.ToString([Globalization.CultureInfo]::InvariantCulture))
        }
        if ($PSBoundParameters.ContainsKey('WorkerThreads')) {
            $arguments.Add('--workers'); $arguments.Add($WorkerThreads.ToString([Globalization.CultureInfo]::InvariantCulture))
        }
        if ($PSBoundParameters.ContainsKey('MinimumFileSize')) {
            $arguments.Add('--min-size'); $arguments.Add($MinimumFileSize.ToString([Globalization.CultureInfo]::InvariantCulture))
        }
        if ($PSBoundParameters.ContainsKey('MaximumFileSize')) {
            $arguments.Add('--max-size'); $arguments.Add($MaximumFileSize.ToString([Globalization.CultureInfo]::InvariantCulture))
        }
        if ($ExcludeHidden) { $arguments.Add('--exclude-hidden') }
        if ($ExcludeSystem) { $arguments.Add('--exclude-system') }
        if ($ExcludeTemporary) { $arguments.Add('--exclude-temporary') }
        if ($ExcludeReparsePoints) { $arguments.Add('--exclude-reparse') }
        if ($ForceDirectoryScanner) { $arguments.Add('--force-directory') }
        foreach ($pattern in $ExcludedPattern) { $arguments.Add('--exclude'); $arguments.Add($pattern) }
        foreach ($extension in $ExcludedExtension) { $arguments.Add('--exclude-ext'); $arguments.Add($extension) }

        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $executable
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($argument in $arguments) { $null = $startInfo.ArgumentList.Add($argument) }

        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        try {
            if (-not $process.Start()) { throw [InvalidOperationException]::new('Could not start the Canopy CLI.') }
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            if ($process.ExitCode -notin @(0, 3)) {
                $PSCmdlet.ThrowTerminatingError((New-CanopyErrorRecord $process.ExitCode $stderr $executable))
            }
            if ([string]::IsNullOrWhiteSpace($stdout)) { return }
            try {
                $result = $stdout | ConvertFrom-Json -Depth 100 -ErrorAction Stop
                if ($process.ExitCode -eq 3) {
                    $message = if ([string]::IsNullOrWhiteSpace($stderr)) {
                        'Canopy completed with one or more failed targets.'
                    } else { $stderr.Trim() }
                    $PSCmdlet.WriteWarning($message)
                }
                $result
            }
            catch {
                $PSCmdlet.ThrowTerminatingError([Management.Automation.ErrorRecord]::new(
                    [IO.InvalidDataException]::new('Canopy CLI returned invalid JSON.', $_.Exception),
                    'Canopy.InvalidJson', [Management.Automation.ErrorCategory]::InvalidData, $executable))
            }
        }
        finally {
            $process.Dispose()
        }
    }
}

Export-ModuleMember -Function Invoke-CanopyScan
