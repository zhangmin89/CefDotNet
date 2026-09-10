param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($MyInvocation.UnboundArguments.Count -ne 0 -or !$IsMacOS) { throw 'Run without arguments on macOS.' }
$root = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '../..'))
if ((Get-Location).Path -ne $root) { throw "Run from the repository root: $root" }
$directory = Join-Path -Path $root -ChildPath 'tests/CefGlue.Avalonia.Tests/bin/Release/net8.0'
$probe = Join-Path -Path $PSScriptRoot -ChildPath 'probe-macos-native.py'
if (!(Test-Path -LiteralPath $directory -PathType Container) -or !(Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'Build the Avalonia tests before collecting native diagnostics.' }
$results = Join-Path -Path $root -ChildPath "artifacts/test-results/native-libraries/$([Guid]::NewGuid().ToString('N'))"
foreach ($mode in @('avalonia', 'cef', 'combined')) {
    $parameters = @{
        FilePath = 'python3'
        ArgumentList = @($probe, $mode, $directory)
        ResultsDirectory = (Join-Path -Path $results -ChildPath $mode)
        DumpToolPath = (Join-Path -Path $root -ChildPath 'artifacts/tools/dotnet-dump')
        TimeoutSeconds = 60
    }
    & (Join-Path -Path $PSScriptRoot -ChildPath 'Invoke-TestMonitor.ps1') @parameters
    $probeExit = $LASTEXITCODE
    if ($probeExit -ne 0) { exit $probeExit }
}
