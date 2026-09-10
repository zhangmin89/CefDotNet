param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($MyInvocation.UnboundArguments.Count -ne 0) { throw 'Unexpected arguments.' }
$source = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '../TestProcessTree.ps1'))
if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing $source" }
. $source

function Assert-Selected {
    param([string]$Case, [hashtable]$Parents, [hashtable]$Records, [object[]]$Seeds, [int[]]$Expected)
    $parameters = @{ Parents = $Parents; Seeds = $Seeds; ReadProcess = { param([int]$Id) $Records[$Id] } }
    $actual = @(Select-TestProcesses @parameters | ForEach-Object -Process { $_.Id })
    if (($actual -join ',') -ne (($Expected | Sort-Object) -join ',')) { throw "$Case expected [$($Expected -join ',')], actual [$($actual -join ',')]" }
    Write-Output "VERIFIED $Case"
}

$root = [pscustomobject]@{ Id = 100; StartTicks = 1000L }
$child = [pscustomobject]@{ Id = 200; StartTicks = 1100L }
$grandchild = [pscustomobject]@{ Id = 300; StartTicks = 1200L }
$older = [pscustomobject]@{ Id = 400; StartTicks = 900L }
$unrelated = [pscustomobject]@{ Id = 500; StartTicks = 950L }
$records = @{ 100 = $root; 200 = $child; 300 = $grandchild; 400 = $older; 500 = $unrelated }
$parameters = @{ Case = 'Reject stale parent PID and its descendants'; Parents = @{ 200 = 100; 300 = 200; 400 = 100; 500 = 400 }; Records = $records; Seeds = @($root); Expected = @(100, 200, 300) }
Assert-Selected @parameters
$parameters = @{ Case = 'Reject reused root PID'; Parents = @{ 200 = 100 }; Records = $records; Seeds = @([pscustomobject]@{ Id = 100; StartTicks = 500L }); Expected = @() }
Assert-Selected @parameters
$parameters = @{ Case = 'Keep verified orphan and discover its new child'; Parents = @{ 300 = 200 }; Records = @{ 200 = $child; 300 = $grandchild }; Seeds = @($root, $child); Expected = @(200, 300) }
Assert-Selected @parameters
$parameters = @{ Case = 'Reject reused known child PID'; Parents = @{ 300 = 200 }; Records = @{ 200 = $child; 300 = $grandchild }; Seeds = @([pscustomobject]@{ Id = 200; StartTicks = 700L }); Expected = @() }
Assert-Selected @parameters
$parameters = @{ Case = 'Ignore processes that exited during enumeration'; Parents = @{ 200 = 100 }; Records = @{ 100 = $root }; Seeds = @($root); Expected = @(100) }
Assert-Selected @parameters
