param([Parameter(Mandatory = $true)][string]$DumpToolPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($MyInvocation.UnboundArguments.Count -ne 0) { throw 'Unexpected arguments.' }
if (!(Test-Path -LiteralPath $DumpToolPath -PathType Leaf)) { throw "Missing dump tool: $DumpToolPath" }
$dumpTool = [IO.Path]::GetFullPath($DumpToolPath)
$monitor = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '../Invoke-TestMonitor.ps1'))
$fixture = Join-Path -Path $PSScriptRoot -ChildPath 'MonitorFixture.ps1'
foreach ($inputPath in @($monitor, $fixture)) {
    if (!(Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Missing input: $inputPath" }
}
$testRoot = [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location).Path, 'artifacts/monitor-self-test-' + [Guid]::NewGuid().ToString('N')))
Write-Output "CREATE $testRoot"
$null = New-Item -ItemType Directory -Path $testRoot
foreach ($mode in @('success', 'failure', 'sequence', 'hang')) {
    $casePath = Join-Path -Path $testRoot -ChildPath "$mode.json"
    $resultPath = Join-Path -Path $testRoot -ChildPath $mode
    $parameters = @{ FilePath = 'pwsh'; ArgumentList = @('-NoProfile', '-File', $fixture, '-Mode', $mode, '-OutputDirectory', $testRoot); ResultsDirectory = $resultPath; DumpToolPath = $dumpTool; TimeoutSeconds = 15 }
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
& $Monitor @parameters
$runExit = $LASTEXITCODE
exit $runExit
'@ | Set-Content -LiteralPath $launcherPath -Encoding utf8
    & pwsh -NoProfile -File $launcherPath -CaseFile $casePath -Monitor $monitor
    $actualExit = $LASTEXITCODE
    $expectedExit = switch ($mode) { 'success' { 0 } 'failure' { 7 } 'sequence' { 0 } 'hang' { 124 } }
    if ($actualExit -ne $expectedExit) { throw "$mode exit: expected $expectedExit, actual $actualExit" }
    $state = Get-Content -LiteralPath (Join-Path -Path $resultPath -ChildPath 'monitor.json') -Raw | ConvertFrom-Json
    if ($state.ExitCode -ne $expectedExit -or $null -eq $state.TestExitCode) { throw "Missing final process status for $mode" }
    if (!(Get-Content -LiteralPath $state.Stdout -Raw).Contains("fixture stdout: $mode")) { throw "Missing stdout for $mode" }
    if (!(Get-Content -LiteralPath $state.Stderr -Raw).Contains("fixture stderr: $mode")) { throw "Missing stderr for $mode" }
    if ($mode -eq 'hang') {
        if ($state.Reason -ne 'test-timeout' -or $state.ActiveTests[0].Name -ne 'Fixture.hang') { throw "Wrong timeout attribution: $($state.Reason)" }
        $child = Get-Content -LiteralPath (Join-Path -Path $testRoot -ChildPath 'child.json') -Raw | ConvertFrom-Json
        try {
            $remaining = [Diagnostics.Process]::GetProcessById($child.Id)
            if ($remaining.StartTime.ToUniversalTime().Ticks -eq $child.StartTicks) { throw "Child PID $($child.Id) survived timeout." }
        } catch [ArgumentException] { }
        $diagnostics = @(Get-Content -LiteralPath (Join-Path -Path $resultPath -ChildPath 'diagnostics.json') -Raw | ConvertFrom-Json)
        if (@($diagnostics | Where-Object -FilterScript { $_.Kind -eq 'dump' -and $_.Success }).Count -lt 2) { throw 'Expected real dumps of both the hung parent and child.' }
    }
    Write-Output "VERIFIED $mode; exit=$actualExit"
}
Write-Output "VERIFIED normal exit, failure propagation, sequential tests exceeding the aggregate limit, per-test timeout despite continued output, real dump collection and child termination. Results: $testRoot"
