param([Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$PackagePath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($MyInvocation.UnboundArguments.Count -ne 0) { throw 'Only -PackagePath is accepted.' }
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "Missing package: $PackagePath" }
$package = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
try {
    $assets = @($package.Entries | Where-Object -Property FullName -Like 'tools/browser-process/*')
    if ($assets.Count -ne 8) { throw "Expected 8 managed payload files, found $($assets.Count)." }
    foreach ($entry in $assets) {
        if ($entry.FullName -notmatch '\.(dll|pdb|deps\.json|runtimeconfig\.json)$') { throw "Unexpected payload: $($entry.FullName)" }
        $stream = $entry.Open()
        try {
            if ($entry.FullName.EndsWith('.dll')) {
                $buffer = [IO.MemoryStream]::new()
                try {
                    $stream.CopyTo($buffer)
                    $buffer.Position = 0
                    $pe = [System.Reflection.PortableExecutable.PEReader]::new($buffer)
                    try {
                        $machine = $pe.PEHeaders.CoffHeader.Machine
                        $flags = [int]$pe.PEHeaders.CorHeader.Flags
                        if ($machine -ne [System.Reflection.PortableExecutable.Machine]::I386 -or ($flags -band 1) -ne 1 -or ($flags -band 0x20002) -ne 0) { throw "Payload is not AnyCPU: $($entry.FullName), machine=$machine flags=$flags" }
                        Write-Output "ANYCPU $($entry.FullName) CorFlags=$flags"
                    } finally { $pe.Dispose() }
                } finally { $buffer.Dispose() }
            } elseif ($entry.FullName.EndsWith('.runtimeconfig.json')) {
                $reader = [IO.StreamReader]::new($stream)
                try { $runtimeConfig = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                if ($runtimeConfig.runtimeOptions.tfm -ne 'net8.0' -or $runtimeConfig.runtimeOptions.rollForward -ne 'Major') { throw 'Incorrect BrowserProcess runtime selection.' }
                Write-Output 'RUNTIME_CONFIG net8.0 RollForward=Major'
            }
        } finally { $stream.Dispose() }
    }
    foreach ($extension in @('props', 'targets')) {
        $entry = $package.GetEntry('buildTransitive/CefGlue.Common.' + $extension)
        if ($null -eq $entry) { throw "Missing buildTransitive .$extension" }
        $stream = $entry.Open()
        try { $packageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        $sourceDirectory = Join-Path -Path $PSScriptRoot -ChildPath '../src/CefGlue.Common/buildTransitive'
        $sourcePath = Join-Path -Path $sourceDirectory -ChildPath ('CefGlue.Common.' + $extension)
        if ($packageHash -ne (Get-FileHash -LiteralPath $sourcePath).Hash) { throw "Packaged .$extension differs from source." }
        Write-Output "MATCHES_SOURCE $($entry.FullName)"
    }
} finally { $package.Dispose() }
