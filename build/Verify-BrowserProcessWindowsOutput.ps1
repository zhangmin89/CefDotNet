param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Directory,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [string]$PackagePath,
    [string]$MainDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($MyInvocation.UnboundArguments.Count -ne 0) { throw 'Unexpected arguments.' }
if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw "Missing consumer output: $Directory" }
if ($PackagePath -and -not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "Missing package: $PackagePath" }
if ($MainDirectory -and -not (Test-Path -LiteralPath $MainDirectory -PathType Container)) { throw "Missing main output: $MainDirectory" }
$names = @('Xilium.CefGlue.BrowserProcess.dll', 'Xilium.CefGlue.Common.Shared.dll', 'Xilium.CefGlue.dll')
$expectedMachine = [Reflection.PortableExecutable.Machine]::Amd64
if ($RuntimeIdentifier -eq 'win-arm64') { $expectedMachine = [Reflection.PortableExecutable.Machine]::Arm64 }
$nativeDirectory = $Directory
if ($MainDirectory) { $nativeDirectory = $MainDirectory }
$nativeDirectory = (Resolve-Path -LiteralPath $nativeDirectory).Path
$cefPath = Join-Path -Path $nativeDirectory -ChildPath 'libcef.dll'
$cefCopies = @(Get-ChildItem -LiteralPath $nativeDirectory -Filter 'libcef.dll' -Recurse -File)
if ($cefCopies.Count -ne 1 -or $cefCopies[0].FullName -ne $cefPath) { throw "Expected one libcef.dll alongside the main application; found $($cefCopies.Count): $(@($cefCopies | Select-Object -ExpandProperty FullName) -join ', ')" }
$stream = [IO.File]::OpenRead($cefPath)
$pe = [Reflection.PortableExecutable.PEReader]::new($stream)
try {
    if ($pe.PEHeaders.CoffHeader.Machine -ne $expectedMachine) { throw "Incorrect CEF machine: $($pe.PEHeaders.CoffHeader.Machine); expected $expectedMachine" }
    Write-Output "CEF_NATIVE $RuntimeIdentifier Copies=1 $cefPath"
} finally { $pe.Dispose(); $stream.Dispose() }
foreach ($name in $names) {
    $path = Join-Path -Path $Directory -ChildPath $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing managed payload: $path" }
    $stream = [IO.File]::OpenRead($path)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $machine = $pe.PEHeaders.CoffHeader.Machine
        if ($null -eq $pe.PEHeaders.CorHeader) { throw "Not a managed assembly: $path" }
        $flags = [int]$pe.PEHeaders.CorHeader.Flags
        $anyCpu = $machine -eq [Reflection.PortableExecutable.Machine]::I386 -and ($flags -band 1) -eq 1 -and ($flags -band 0x20002) -eq 0
        if ($name -eq 'Xilium.CefGlue.BrowserProcess.dll') {
            if (-not $anyCpu) { throw "BrowserProcess is not AnyCPU: $path machine=$machine flags=$flags" }
            Write-Output "ANYCPU $path CorFlags=$flags"
        } else {
            if (-not $anyCpu -and $machine -ne $expectedMachine) { throw "Shared dependency architecture mismatch: $path machine=$machine expected=$expectedMachine" }
            Write-Output "SHARED_DEPENDENCY $name machine=$machine CorFlags=$flags"
        }
    } finally { $pe.Dispose(); $stream.Dispose() }
}
$runtimePath = Join-Path -Path $Directory -ChildPath 'Xilium.CefGlue.BrowserProcess.runtimeconfig.json'
$runtime = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.tfm -ne 'net8.0' -or $runtime.runtimeOptions.rollForward -ne 'Major') { throw 'Unexpected runtime selection.' }
$hostPath = Join-Path -Path $Directory -ChildPath 'Xilium.CefGlue.BrowserProcess.exe'
if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw "Missing apphost: $hostPath" }
$stream = [IO.File]::OpenRead($hostPath)
$pe = [Reflection.PortableExecutable.PEReader]::new($stream)
try {
    if ($pe.PEHeaders.CoffHeader.Machine -ne $expectedMachine) { throw "Incorrect apphost machine: $($pe.PEHeaders.CoffHeader.Machine)" }
    if ($pe.PEHeaders.PEHeader.SizeOfStackReserve -ne 8388608) { throw 'Apphost stack reserve is not 8 MiB.' }
    if ($pe.PEHeaders.PEHeader.Subsystem -ne [Reflection.PortableExecutable.Subsystem]::WindowsGui) { throw 'Apphost is not Windows GUI.' }
    Write-Output "APPHOST $RuntimeIdentifier StackReserve=8388608 Subsystem=WindowsGui $hostPath"
} finally { $pe.Dispose(); $stream.Dispose() }
if ($MainDirectory) {
    foreach ($name in @('Xilium.CefGlue.Common.dll', 'Xilium.CefGlue.Common.Shared.dll', 'Xilium.CefGlue.dll')) {
        $path = Join-Path -Path $MainDirectory -ChildPath $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing main dependency: $path" }
        $stream = [IO.File]::OpenRead($path)
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $machine = $pe.PEHeaders.CoffHeader.Machine
            if ($null -eq $pe.PEHeaders.CorHeader) { throw "Not a managed dependency: $path" }
            $flags = [int]$pe.PEHeaders.CorHeader.Flags
            $anyCpu = $machine -eq [Reflection.PortableExecutable.Machine]::I386 -and ($flags -band 1) -eq 1 -and ($flags -band 0x20002) -eq 0
            if (-not $anyCpu -and $machine -ne $expectedMachine) { throw "Main dependency architecture mismatch: $path machine=$machine expected=$expectedMachine" }
            Write-Output "MAIN_DEPENDENCY $name machine=$machine CorFlags=$flags"
        } finally { $pe.Dispose(); $stream.Dispose() }
    }
}
if ($PackagePath) {
    $package = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
    try {
        $payloadNames = @($names) + @('Xilium.CefGlue.BrowserProcess.deps.json', 'Xilium.CefGlue.BrowserProcess.runtimeconfig.json')
        foreach ($name in $payloadNames) {
            $packageDirectory = 'tools/browser-process/'
            if ($name -in @('Xilium.CefGlue.Common.Shared.dll', 'Xilium.CefGlue.dll')) { $packageDirectory = 'lib/' + $runtime.runtimeOptions.tfm + '/' }
            $entry = $package.GetEntry($packageDirectory + $name)
            if ($null -eq $entry) { throw "Missing package payload: $name" }
            $stream = $entry.Open()
            try { $packageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            $path = Join-Path -Path $Directory -ChildPath $name
            if ((Get-FileHash -LiteralPath $path).Hash -ne $packageHash) { throw "Consumer payload differs from package: $path" }
            Write-Output "MATCHES_PACKAGE $name"
        }
    } finally { $package.Dispose() }
}
