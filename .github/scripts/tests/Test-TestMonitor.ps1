param([Parameter(Mandatory = $true)][string]$DumpToolPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
& (Join-Path -Path $PSScriptRoot -ChildPath 'Test-TestProcessTree.ps1')
if ($MyInvocation.UnboundArguments.Count -ne 0) { throw 'Unexpected arguments.' }
if (!(Test-Path -LiteralPath $DumpToolPath -PathType Leaf)) { throw "Missing dump tool: $DumpToolPath" }
$dumpTool = [IO.Path]::GetFullPath($DumpToolPath)
$monitor = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '../Invoke-TestMonitor.ps1'))
$fixtureProject = Join-Path -Path $PSScriptRoot -ChildPath 'MonitorFixture/MonitorFixture.csproj'
foreach ($inputPath in @($monitor, $fixtureProject)) {
    if (!(Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Missing input: $inputPath" }
}
$testRoot = [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location).Path, 'artifacts/monitor-self-test-' + [Guid]::NewGuid().ToString('N')))
Write-Output "CREATE $testRoot"
$null = New-Item -ItemType Directory -Path $testRoot
# Exercise the repository's .NET runtime on every OS, not the runner's bundled PowerShell runtime.
$fixtureArtifacts = Join-Path -Path $testRoot -ChildPath 'fixture'
& dotnet build $fixtureProject -c Release --artifacts-path $fixtureArtifacts -m:1 -nr:false
$buildExit = $LASTEXITCODE
if ($buildExit -ne 0) { throw "Monitor fixture build failed: exit $buildExit" }
$fixture = Join-Path -Path $fixtureArtifacts -ChildPath 'bin/MonitorFixture/release/MonitorFixture.dll'
if (!(Test-Path -LiteralPath $fixture -PathType Leaf)) { throw "Missing built fixture: $fixture" }
foreach ($mode in @('success', 'failure', 'success-exited', 'failure-exited', 'sequence', 'hang')) {
    $fixtureMode = $mode.Split('-')[0]
    $casePath = Join-Path -Path $testRoot -ChildPath "$mode.json"
    $resultPath = Join-Path -Path $testRoot -ChildPath $mode
    $parameters = @{ FilePath = 'dotnet'; ArgumentList = @($fixture, $fixtureMode, $testRoot); ResultsDirectory = $resultPath; DumpToolPath = $dumpTool; TimeoutSeconds = 15 }
    $parameters | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $casePath -Encoding utf8
    # Use JSON across the process boundary, then splat the argument array in one PowerShell process.
    $launcherPath = Join-Path -Path $testRoot -ChildPath "$mode.ps1"
    @'
param([Parameter(Mandatory = $true)][string]$CaseFile, [Parameter(Mandatory = $true)][string]$Monitor)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($MyInvocation.UnboundArguments.Count -ne 0) { throw 'Unexpected arguments.' }
foreach ($path in @($CaseFile, $Monitor)) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing input: $path" } }
$parameters = Get-Content -LiteralPath $CaseFile -Raw | ConvertFrom-Json -AsHashtable
if ([IO.Path]::GetFileNameWithoutExtension($CaseFile).EndsWith('-exited')) {
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Monitor, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw 'Invalid monitor syntax.' }
    $launch = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$capture' -and $node.Right.Extent.Text.StartsWith('Start-CapturedProcess ') }, $false))
    if ($launch.Count -ne 1) { throw 'Expected exactly one test process launch.' }
    # Reproduce Unix losing StartTime after exit while keeping the real process, exit code and pipes.
    $null = Set-PSBreakpoint -Script $Monitor -Line ($launch[0].Extent.EndLineNumber + 1) -Action {
        $exiting = $capture.Process
        Write-Host "STARTUP BEFORE PID $($exiting.Id); ALIVE $(!$exiting.HasExited); CPU $($exiting.TotalProcessorTime); STDOUT $($capture.Streams[0].Length); STDERR $($capture.Streams[1].Length)"
        if (!$exiting.WaitForExit(10000)) { throw 'Short-lived fixture did not exit.' }
        Write-Host "STARTUP AFTER PID $($exiting.Id); EXIT_CODE $($exiting.ExitCode); CPU $($exiting.TotalProcessorTime); STDOUT $($capture.Streams[0].Length); STDERR $($capture.Streams[1].Length)"
        Add-Member -InputObject $exiting -MemberType ScriptProperty -Name StartTime -Value { $null } -Force
        $evidence = @{ Pid = $exiting.Id; ExitCode = $exiting.ExitCode; HasStartTime = $null -ne $exiting.StartTime }
        $evidence | ConvertTo-Json | Set-Content -LiteralPath (Join-Path -Path (Split-Path -Path $capture.Stdout -Parent) -ChildPath 'startup-exit.json') -Encoding utf8
    }
}
& $Monitor @parameters
$runExit = $LASTEXITCODE
exit $runExit
'@ | Set-Content -LiteralPath $launcherPath -Encoding utf8
    & pwsh -NoProfile -File $launcherPath -CaseFile $casePath -Monitor $monitor
    $actualExit = $LASTEXITCODE
    $expectedExit = switch ($fixtureMode) { 'success' { 0 } 'failure' { 7 } 'sequence' { 0 } 'hang' { 124 } }
    if ($actualExit -ne $expectedExit) { throw "$mode exit: expected $expectedExit, actual $actualExit" }
    $state = Get-Content -LiteralPath (Join-Path -Path $resultPath -ChildPath 'monitor.json') -Raw | ConvertFrom-Json
    if ($state.ExitCode -ne $expectedExit -or $null -eq $state.TestExitCode) { throw "Missing final process status for $mode" }
    if ($mode.EndsWith('-exited')) {
        $startup = Get-Content -LiteralPath (Join-Path -Path $resultPath -ChildPath 'startup-exit.json') -Raw | ConvertFrom-Json
        if ($startup.HasStartTime -or $startup.Pid -ne $state.Pid -or $startup.ExitCode -ne $expectedExit -or $state.TestExitCode -ne $expectedExit -or $state.Reason -ne 'exited') { throw "Early exit was not preserved for $mode" }
    }
    if (!(Get-Content -LiteralPath $state.Stdout -Raw).Contains("fixture stdout: $fixtureMode")) { throw "Missing stdout for $mode" }
    if (!(Get-Content -LiteralPath $state.Stderr -Raw).Contains("fixture stderr: $fixtureMode")) { throw "Missing stderr for $mode" }
    if ($mode -eq 'hang') {
        if ($state.Reason -ne 'test-timeout' -or $state.ActiveTests[0].Name -ne 'Fixture.hang') { throw "Wrong timeout attribution: $($state.Reason)" }
        $child = Get-Content -LiteralPath (Join-Path -Path $testRoot -ChildPath 'child.json') -Raw | ConvertFrom-Json
        try {
            $remaining = [Diagnostics.Process]::GetProcessById($child.Id)
            if ($remaining.StartTime.ToUniversalTime().Ticks -eq $child.StartTicks) { throw "Child PID $($child.Id) survived timeout." }
        } catch [ArgumentException] { }
        $diagnostics = @(Get-Content -LiteralPath (Join-Path -Path $resultPath -ChildPath 'diagnostics.json') -Raw | ConvertFrom-Json)
        foreach ($targetId in @($state.Pid, $child.Id)) {
            if (@($diagnostics | Where-Object -FilterScript { $_.Target -eq $targetId -and $_.Kind -eq 'dump' -and $_.Success }).Count -ne 1) { throw "Expected a real dump of fixture PID $targetId." }
        }
    }
    Write-Output "VERIFIED $mode; exit=$actualExit"
}
Write-Output "VERIFIED normal exit, failure propagation, sequential tests exceeding the aggregate limit, per-test timeout despite continued output, real dump collection and child termination. Results: $testRoot"
exit 0
