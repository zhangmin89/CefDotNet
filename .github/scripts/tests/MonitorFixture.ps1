param(
    [Parameter(Mandatory = $true)][ValidateSet('success', 'failure', 'sequence', 'hang', 'child')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($MyInvocation.UnboundArguments.Count -ne 0) { throw 'Unexpected arguments.' }
if (!(Test-Path -LiteralPath $OutputDirectory -PathType Container)) { throw "Missing output directory: $OutputDirectory" }
Write-Output "fixture stdout: $Mode"
[Console]::Error.WriteLine("fixture stderr: $Mode")
if ($Mode -eq 'success') { exit 0 }
if ($Mode -eq 'failure') { exit 7 }
function Write-TestEvent {
    param([string]$Id, [string]$EventName)
    $path = Join-Path -Path $env:CEFGLUE_TEST_DIAGNOSTICS_DIR -ChildPath "test-events-$PID.jsonl"
    @{ Event = $EventName; Id = $Id; Name = "Fixture.$Id"; IsSuite = $false; Pid = $PID; Timestamp = [Diagnostics.Stopwatch]::GetTimestamp(); Frequency = [Diagnostics.Stopwatch]::Frequency; ElapsedMs = 0 } | ConvertTo-Json -Compress | Add-Content -LiteralPath $path -Encoding utf8
}
if ($Mode -eq 'sequence') {
    foreach ($id in @('first', 'second')) {
        Write-TestEvent -Id $id -EventName 'start'
        [Threading.Thread]::Sleep(10000)
        Write-TestEvent -Id $id -EventName 'end'
    }
    exit 0
}
if ($Mode -eq 'hang') {
    $info = [Diagnostics.ProcessStartInfo]::new('pwsh')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in @('-NoProfile', '-File', $PSCommandPath, '-Mode', 'child', '-OutputDirectory', $OutputDirectory)) { $info.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($info)
    @{ Id = $child.Id; StartTicks = $child.StartTime.ToUniversalTime().Ticks } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path -Path $OutputDirectory -ChildPath 'child.json') -Encoding utf8
    Write-TestEvent -Id 'hang' -EventName 'start'
    while ($true) {
        Write-Output 'Fixture.hang still produces output, but has not completed.'
        [Threading.Thread]::Sleep(10000)
    }
}
[Threading.Thread]::Sleep([Threading.Timeout]::Infinite)
