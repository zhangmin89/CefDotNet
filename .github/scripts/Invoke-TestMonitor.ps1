param(
    [Parameter(Mandatory = $true)][string]$FilePath,
    [Parameter(Mandatory = $true)][string[]]$ArgumentList,
    [Parameter(Mandatory = $true)][string]$ResultsDirectory,
    [Parameter(Mandatory = $true)][string]$DumpToolPath,
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 600
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path -Path $PSScriptRoot -ChildPath 'TestProcessTree.ps1')
if ($MyInvocation.UnboundArguments.Count -ne 0 -or $ArgumentList.Count -eq 0) { throw 'Expected a nonempty command argument array.' }
$workingDirectory = (Get-Location).Path
$resultRoot = [IO.Path]::GetFullPath($ResultsDirectory)
$dumpTool = [IO.Path]::GetFullPath($DumpToolPath)
if (!(Test-Path -LiteralPath $dumpTool -PathType Leaf)) { throw "Missing dump tool: $dumpTool" }
if (!$resultRoot.StartsWith($workingDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Results must be inside the working directory: $resultRoot" }
Write-Output "CREATE $resultRoot"
if (Test-Path -LiteralPath $resultRoot) { throw "Results already exist: $resultRoot" }
$null = New-Item -ItemType Directory -Path $resultRoot

function Start-CapturedProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$Prefix, [bool]$TestProcess = $false)
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $workingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    if ($TestProcess) {
        $info.Environment['CEFGLUE_TEST_DIAGNOSTICS_DIR'] = $resultRoot
        $info.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    }
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $stdoutPath = Join-Path -Path $resultRoot -ChildPath "$Prefix.stdout.log"
    $stderrPath = Join-Path -Path $resultRoot -ChildPath "$Prefix.stderr.log"
    $stdout = [IO.FileStream]::new($stdoutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, $true)
    $stderr = [IO.FileStream]::new($stderrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, $true)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (!$process.Start()) { throw "Could not start $Executable" }
    } catch {
        $stdout.Dispose()
        $stderr.Dispose()
        throw
    }
    return [pscustomobject]@{
        Process = $process; Stdout = $stdoutPath; Stderr = $stderrPath
        Streams = @($stdout, $stderr)
        Copies = @($process.StandardOutput.BaseStream.CopyToAsync($stdout), $process.StandardError.BaseStream.CopyToAsync($stderr))
    }
}

function Close-Capture {
    param($Capture)
    if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$Capture.Copies, 10000)) { throw "Output pipes did not close for PID $($Capture.Process.Id)" }
    foreach ($stream in $Capture.Streams) { $stream.Dispose() }
}

$knownProcesses = @{}
function Get-TestProcesses {
    param([int]$RootProcessId, [long]$RootStartTicks, [hashtable]$Known = $knownProcesses)
    $parents = @{}
    if ($IsWindows) {
        foreach ($item in Get-CimInstance -ClassName Win32_Process) { $parents[[int]$item.ProcessId] = [int]$item.ParentProcessId }
    } else {
        $rows = & /bin/ps -axo pid=,ppid=
        $psExit = $LASTEXITCODE
        if ($psExit -ne 0) { throw "Process enumeration failed: exit $psExit" }
        foreach ($row in $rows) {
            $values = $row.Trim() -split '\s+'
            $parents[[int]$values[0]] = [int]$values[1]
        }
    }
    $readProcess = {
        param([int]$Id)
        $candidate = $null
        try {
            $candidate = [Diagnostics.Process]::GetProcessById($Id)
            [pscustomobject]@{ Id = $Id; Name = $candidate.ProcessName; StartTicks = $candidate.StartTime.ToUniversalTime().Ticks; CpuMs = $candidate.TotalProcessorTime.TotalMilliseconds }
        } catch [ArgumentException] { } # A process may exit between snapshots.
        finally { if ($null -ne $candidate) { $candidate.Dispose() } }
    }
    $seeds = @([pscustomobject]@{ Id = $RootProcessId; StartTicks = $RootStartTicks }) + @($Known.Values)
    foreach ($entry in @(Select-TestProcesses -Parents $parents -Seeds $seeds -ReadProcess $readProcess)) {
        $Known[$entry.Id] = $entry
        $entry
    }
}

function Stop-TestProcesses {
    param([object[]]$Processes)
    foreach ($entry in ($Processes | Sort-Object -Property StartTicks -Descending)) {
        $candidate = $null
        try {
            $candidate = [Diagnostics.Process]::GetProcessById($entry.Id)
            # Never act on a PID that the OS has already reused.
            if ($candidate.StartTime.ToUniversalTime().Ticks -eq $entry.StartTicks) {
                # Kill only verified identities; recursive Kill would walk raw parent PIDs again.
                $candidate.Kill()
                if (!$candidate.WaitForExit(10000)) { throw "PID $($entry.Id) did not exit after termination." }
            }
        } catch [ArgumentException] { }
        finally { if ($null -ne $candidate) { $candidate.Dispose() } }
    }
}

function Save-Diagnostics {
    param([object[]]$Processes)
    $Processes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path -Path $resultRoot -ChildPath 'processes.json') -Encoding utf8
    $collectors = @()
    foreach ($entry in $Processes) {
        if ($entry.Name -match '(?i)dotnet|testhost|cefglue|pwsh') {
            $dumpPath = Join-Path -Path $resultRoot -ChildPath "process-$($entry.Id).dmp"
            $capture = Start-CapturedProcess -Executable $dumpTool -Arguments @('collect', '--process-id', [string]$entry.Id, '--type', 'Mini', '--output', $dumpPath) -Prefix "dump-$($entry.Id)"
            $collectors += [pscustomobject]@{ Capture = $capture; Target = $entry.Id; Kind = 'dump'; Artifact = $dumpPath }
            if ($IsMacOS) {
                $samplePath = Join-Path -Path $resultRoot -ChildPath "sample-$($entry.Id).txt"
                $capture = Start-CapturedProcess -Executable '/usr/bin/sample' -Arguments @([string]$entry.Id, '1', '-file', $samplePath) -Prefix "sample-$($entry.Id)"
                $collectors += [pscustomobject]@{ Capture = $capture; Target = $entry.Id; Kind = 'sample'; Artifact = $samplePath }
            }
        }
    }
    try {
        if ($collectors.Count -gt 0) {
            $tasks = [Threading.Tasks.Task[]]@($collectors | ForEach-Object -Process { $_.Capture.Process.WaitForExitAsync() })
            $null = [Threading.Tasks.Task]::WaitAll($tasks, 60000)
        }
    } finally {
        $statuses = @(foreach ($collector in $collectors) {
            $process = $collector.Capture.Process
            $timedOut = !$process.HasExited
            if ($timedOut) {
                $collectorTargets = @(Get-TestProcesses -RootProcessId $process.Id -RootStartTicks $process.StartTime.ToUniversalTime().Ticks -Known @{})
                Stop-TestProcesses -Processes $collectorTargets
                if (!$process.WaitForExit(10000)) { throw "Collector PID $($process.Id) did not exit." }
            }
            Close-Capture -Capture $collector.Capture
            $hasArtifact = (Test-Path -LiteralPath $collector.Artifact -PathType Leaf) -and (Get-Item -LiteralPath $collector.Artifact).Length -gt 0
            [pscustomobject]@{ Target = $collector.Target; CollectorPid = $process.Id; Kind = $collector.Kind; TimedOut = $timedOut; ExitCode = $process.ExitCode; Artifact = $collector.Artifact; Success = !$timedOut -and $process.ExitCode -eq 0 -and $hasArtifact }
        })
        $statuses | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path -Path $resultRoot -ChildPath 'diagnostics.json') -Encoding utf8
        $statuses | Format-Table -Property Target, Kind, TimedOut, ExitCode, Success | Out-String | Write-Output
    }
}

$positions = @{}
$eventCounts = @{}
$activeTests = @{}
$activeSuites = @{}
$timing = @{ LastBoundary = [Diagnostics.Stopwatch]::GetTimestamp() }
function Read-TestEvents {
    foreach ($file in Get-ChildItem -LiteralPath $resultRoot -Filter 'test-events-*.jsonl' -File) {
        if (!$eventCounts.ContainsKey($file.FullName)) { $eventCounts[$file.FullName] = 0 }
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $reader = [IO.StreamReader]::new($stream)
        try { $lines = $reader.ReadToEnd().Split("`n") }
        finally { $reader.Dispose() }
        # Only consume complete lines. The writer may still be appending the final record.
        for ($index = $eventCounts[$file.FullName]; $index -lt $lines.Length - 1; $index++) {
            $event = $lines[$index] | ConvertFrom-Json
            if ($event.Frequency -ne [Diagnostics.Stopwatch]::Frequency) { throw 'Test and monitor clocks have different frequencies.' }
            $key = "$($event.Pid):$($event.Id)"
            $active = if ($event.IsSuite) { $activeSuites } else { $activeTests }
            if ($event.Event -eq 'start') { $active[$key] = $event }
            elseif ($event.Event -eq 'end') { $active.Remove($key) }
            else { throw "Unknown test event: $($event.Event)" }
            $timing.LastBoundary = [Math]::Max($timing.LastBoundary, [long]$event.Timestamp)
            Write-Output "[test-monitor] $($event.Event) $($event.Name); elapsedMs=$($event.ElapsedMs)"
        }
        $eventCounts[$file.FullName] = $lines.Length - 1
    }
}

function Show-CapturedOutput {
    param($Capture)
    foreach ($path in @($Capture.Stdout, $Capture.Stderr)) {
        if (!$positions.ContainsKey($path)) { $positions[$path] = 0L }
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $stream.Position = $positions[$path]
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
            $text = $reader.ReadToEnd()
            $positions[$path] = $stream.Position
            $reader.Dispose()
            [Console]::Write($text)
        } finally { $stream.Dispose() }
    }
}

$capture = Start-CapturedProcess -Executable $FilePath -Arguments $ArgumentList -Prefix 'test' -TestProcess $true
$process = $capture.Process
$rootStartTicks = $process.StartTime.ToUniversalTime().Ticks
$statePath = Join-Path -Path $resultRoot -ChildPath 'monitor.json'
$state = [ordered]@{ Pid = $process.Id; WorkingDirectory = $workingDirectory; Executable = $FilePath; Arguments = $ArgumentList; StartedUtc = [DateTime]::UtcNow.ToString('O'); Reason = 'running'; ExitCode = $null; TestExitCode = $null; ActiveTests = @(); ActiveSuites = @(); Stdout = $capture.Stdout; Stderr = $capture.Stderr }
$state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding utf8
Write-Output "PID $($process.Id); WORKING_DIRECTORY $workingDirectory; STDOUT $($capture.Stdout); STDERR $($capture.Stderr)"
$exitTask = $process.WaitForExitAsync()
$previous = $null
$idle = 0
$targets = @()
try {
    while ($true) {
        Read-TestEvents
        $state.ActiveTests = @($activeTests.Values)
        $state.ActiveSuites = @($activeSuites.Values)
        $targets = @(Get-TestProcesses -RootProcessId $process.Id -RootStartTicks $rootStartTicks)
        $files = @(Get-ChildItem -LiteralPath $resultRoot -File | Where-Object -FilterScript { $_.Extension -in @('.log', '.jsonl') } | Sort-Object -Property Name | ForEach-Object -Process {
            # Windows directory metadata may retain length zero while the writer is open.
            $stream = [IO.File]::Open($_.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try { [pscustomobject]@{ Name = $_.Name; Bytes = $stream.Length; Modified = $_.LastWriteTimeUtc.Ticks } }
            finally { $stream.Dispose() }
        })
        $alive = !$process.HasExited
        $snapshot = [ordered]@{ Alive = $alive; ExitCode = if ($alive) { $null } else { $process.ExitCode }; Processes = $targets; Files = $files } | ConvertTo-Json -Depth 5 -Compress
        Show-CapturedOutput -Capture $capture
        Write-Output "[test-monitor] $snapshot"
        if (!$alive) {
            $state.Reason = 'exited'
            $state.ExitCode = $process.ExitCode
            break
        }
        if ($null -ne $previous) {
            $progress = $snapshot -cne $previous
            Write-Output "[test-monitor] PROGRESS $progress"
            if ($progress) { $idle = 0 } else { $idle++ }
        }
        $watchStart = if ($activeTests.Count -gt 0) { ($activeTests.Values | Measure-Object -Property Timestamp -Minimum).Minimum } else { $timing.LastBoundary }
        $elapsed = [Diagnostics.Stopwatch]::GetElapsedTime([long]$watchStart).TotalSeconds
        if ($elapsed -ge $TimeoutSeconds -or $idle -ge 2) {
            $state.Reason = if ($idle -ge 2) { 'no-progress' } elseif ($activeTests.Count -gt 0) { 'test-timeout' } else { 'between-tests-timeout' }
            $state.ExitCode = 124
            $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding utf8
            Write-Output "::error::Test monitor: $($state.Reason); collecting diagnostics before termination."
            Save-Diagnostics -Processes $targets
            break
        }
        $previous = $snapshot
        $deadline = [Threading.Tasks.Task]::Delay([TimeSpan]::FromSeconds($TimeoutSeconds - $elapsed))
        $null = [Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]@($exitTask, $deadline), 60000)
    }
} finally {
    if ($state.Reason -eq 'running') { $state.Reason = 'interrupted'; $state.ExitCode = 1 }
    try {
        # Refresh before killing so that children created during collection are included.
        $targets = @(Get-TestProcesses -RootProcessId $process.Id -RootStartTicks $rootStartTicks)
        Stop-TestProcesses -Processes $targets
        if (!$process.WaitForExit(10000)) { throw "Test PID $($process.Id) is still alive." }
        $state.TestExitCode = $process.ExitCode
        Close-Capture -Capture $capture
        Read-TestEvents
        $state.ActiveTests = @($activeTests.Values)
        $state.ActiveSuites = @($activeSuites.Values)
        Show-CapturedOutput -Capture $capture
    } catch {
        $state.ExitCode = 1
        Write-Output "::error::Test monitor cleanup failed: $_"
    } finally {
        $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding utf8
        Write-Output "[test-monitor] REASON $($state.Reason); EXIT_CODE $($state.ExitCode); TEST_EXIT_CODE $($state.TestExitCode)"
    }
}
exit $state.ExitCode
