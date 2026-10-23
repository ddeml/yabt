param(
    [Parameter(Mandatory = $true)] [string] $RecordPath,
    [Parameter(Mandatory = $true)] [string[]] $RunnerArguments,
    [ValidateRange(10, 7200)] [int] $TimeoutSeconds = 600,
    [ValidateRange(128, 16384)] [int] $PrivateLimitMiB = 2048
)

$ErrorActionPreference = 'Stop'
$recordFullPath = [IO.Path]::GetFullPath($RecordPath)
if (Test-Path -LiteralPath $recordFullPath) { throw 'Use a fresh watchdog record path.' }
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($recordFullPath))) {
    throw 'Create the record parent directory before running the watchdog.'
}
$runnerPath = Join-Path $PSScriptRoot 'bin/Release/net10.0/Yabt.Profiling.dll'
if (-not [IO.File]::Exists($runnerPath)) { throw 'Build the Release profiling runner first.' }

function Get-ProcessSnapshot {
    $snapshot = @{}
    foreach ($item in [Diagnostics.Process]::GetProcesses()) {
        try {
            if ($item.Id -eq $PID -or $item.Id -eq 0) { continue }
            $snapshot[$item.Id] = @{
                name = $item.ProcessName
                startedTicks = $item.StartTime.ToUniversalTime().Ticks
                cpuSeconds = $item.TotalProcessorTime.TotalSeconds
            }
        }
        catch { }
        finally { $item.Dispose() }
    }
    return $snapshot
}

$before = Get-ProcessSnapshot
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add($runnerPath)
foreach ($argument in $RunnerArguments) { $start.ArgumentList.Add($argument) }
$start.Environment['YABT_PROFILE_MAX_SECONDS'] = [string]($TimeoutSeconds - 5)
$start.Environment['YABT_PROFILE_MAX_PRIVATE_MIB'] = [string]$PrivateLimitMiB
$child = [Diagnostics.Process]::new()
$child.StartInfo = $start
$startedUtc = [DateTimeOffset]::UtcNow
$clock = [Diagnostics.Stopwatch]::StartNew()
$reason = $null
$peakPrivate = 0L
$peakWorkingSet = 0L
$samples = 0L
$childId = $null
try {
    if (-not $child.Start()) { throw 'Could not start the profiling child process.' }
    $childId = $child.Id
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    while (-not $child.WaitForExit(250)) {
        $child.Refresh()
        if ($child.HasExited) { break }
        $samples++
        $peakPrivate = [Math]::Max($peakPrivate, $child.PrivateMemorySize64)
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $child.WorkingSet64)
        if ($peakPrivate -gt $PrivateLimitMiB * 1MB) { $reason = 'private-memory-limit' }
        if ($clock.Elapsed.TotalSeconds -gt $TimeoutSeconds) { $reason = 'elapsed-time-limit' }
        if ($null -ne $reason) {
            # This handle belongs only to the child started above. Fixtures are
            # disposable and may remain incomplete after forced termination.
            $child.Kill($true)
            $child.WaitForExit()
            break
        }
    }
    $clock.Stop()
    $exitCode = $child.ExitCode
    $outputText = $stdout.GetAwaiter().GetResult()
    $errorText = $stderr.GetAwaiter().GetResult()
}
finally {
    if ($null -ne $childId -and -not $child.HasExited) {
        $child.Kill($true)
        $child.WaitForExit()
    }
    $child.Dispose()
}

$after = Get-ProcessSnapshot
$otherCpu = foreach ($entry in $after.GetEnumerator()) {
    if ($entry.Key -eq $childId) { continue }
    $prior = $before[$entry.Key]
    if ($null -eq $prior -or $prior.startedTicks -ne $entry.Value.startedTicks) { continue }
    $delta = $entry.Value.cpuSeconds - $prior.cpuSeconds
    if ($delta -ge 0.1) {
        [pscustomobject]@{ id = $entry.Key; name = $entry.Value.name; cpuSeconds = $delta }
    }
}
$record = [ordered]@{
    schemaVersion = 1
    startedUtc = $startedUtc
    runnerArguments = $RunnerArguments
    processId = $childId
    exitCode = $exitCode
    failure = $reason
    elapsedMillisecondsIncludingStartup = $clock.Elapsed.TotalMilliseconds
    sampledPeakPrivateBytes = $peakPrivate
    sampledPeakWorkingSetBytes = $peakWorkingSet
    samples = $samples
    sampleIntervalMilliseconds = 250
    otherObservedProcessCpu = @($otherCpu | Sort-Object cpuSeconds -Descending)
    stdout = $outputText
    stderr = $errorText
}
$stream = [IO.File]::Open($recordFullPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 6))
    $stream.Write($bytes, 0, $bytes.Length)
}
finally { $stream.Dispose() }
if ($outputText) { Write-Output $outputText.TrimEnd() }
if ($errorText) { Write-Output $errorText.TrimEnd() }
if ($null -ne $reason -or $exitCode -ne 0) {
    throw "Profiling child failed: exit $exitCode; limit '$reason'. See $recordFullPath."
}
