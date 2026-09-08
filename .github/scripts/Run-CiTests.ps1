param([Parameter(Mandatory = $true)][ValidateSet('core', 'build', 'avalonia', 'wpf')][string]$Suite)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($MyInvocation.UnboundArguments.Count -ne 0) { throw 'Unexpected arguments.' }
$root = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '../..'))
if ((Get-Location).Path -ne $root) { throw "Run from the repository root: $root" }
$monitor = Join-Path -Path $PSScriptRoot -ChildPath 'Invoke-TestMonitor.ps1'
$toolName = if ($IsWindows) { 'dotnet-dump.exe' } else { 'dotnet-dump' }
$tool = Join-Path -Path $root -ChildPath "artifacts/tools/$toolName"
$resultDirectory = Join-Path -Path $root -ChildPath "artifacts/test-results/$Suite/$([Guid]::NewGuid().ToString('N'))"
Write-Output "CREATE $resultDirectory"
$null = New-Item -ItemType Directory -Path $resultDirectory
$executable = 'dotnet'
if ($IsLinux -and [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') {
    $tlsShim = Join-Path -Path $root -ChildPath 'artifacts/native/libcef-test-tls.so'
    if (!(Test-Path -LiteralPath $tlsShim -PathType Leaf)) { throw "Missing verified TLS reservation: $tlsShim" }
    $env:GLIBC_TUNABLES = 'glibc.rtld.optional_static_tls=16384'
    $env:LD_PRELOAD = $tlsShim
}
if ($Suite -eq 'wpf' -and !$IsWindows) { throw 'The WPF suite requires Windows.' }
if ($Suite -eq 'avalonia' -and $IsMacOS) {
    $inputPath = 'tests/CefGlue.Avalonia.Tests/bin/Release/net8.0/CefGlue.Avalonia.Tests.dll'
    $arguments = @($inputPath, (Join-Path -Path $resultDirectory -ChildPath 'avalonia.xml'))
} else {
    $inputPath = switch ($Suite) {
        'avalonia' { 'tests/CefGlue.Avalonia.Tests/CefGlue.Avalonia.Tests.csproj' }
        'wpf' { 'tests/CefGlue.WPF.Tests/CefGlue.WPF.Tests.csproj' }
        default { 'tests/CefGlue.Tests/CefGlue.Tests.csproj' }
    }
    # The external monitor owns hang collection and termination. Keep crash blame only.
    $arguments = @('test', $inputPath, '-c', 'Release', '--no-build', '-m:1', '--logger', 'trx', '--results-directory', $resultDirectory, '--blame')
    if ($Suite -eq 'core') { $arguments += @('--filter', 'TestCategory!=BuildIntegration') }
    if ($Suite -eq 'build') { $arguments += @('--filter', 'TestCategory=BuildIntegration') }
    if ($IsLinux) {
        $executable = 'xvfb-run'
        $arguments = @('--auto-servernum', 'dotnet') + $arguments
    }
}
if (!(Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Missing test input: $inputPath" }
$parameters = @{ FilePath = $executable; ArgumentList = $arguments; ResultsDirectory = (Join-Path -Path $resultDirectory -ChildPath 'monitor'); DumpToolPath = $tool; TimeoutSeconds = 600 }
& $monitor @parameters
$testExit = $LASTEXITCODE
if ($testExit -ne 0) { exit $testExit }
$reports = @(Get-ChildItem -LiteralPath $resultDirectory -File | Where-Object -FilterScript { $_.Extension -in @('.trx', '.xml') -and $_.Length -gt 0 })
if ($reports.Count -eq 0) { throw 'Tests exited successfully but no test report was produced.' }
