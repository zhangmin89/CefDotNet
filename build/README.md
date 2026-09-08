# BrowserProcess build verification

The scripts in this directory are reusable checks intended for version control. Run them with PowerShell 7 (`pwsh`). Generated packages, consumer outputs, logs, and temporary test fixtures belong in `artifacts/`.

`CefGlue.Package.targets` packages the managed BrowserProcess assets. Consumer Build and Publish generate the native apphost through `src/CefGlue.Common/buildTransitive/CefGlue.Common.targets`.

## Check a NuGet package

```powershell
pwsh -NoProfile -File build/Verify-BrowserProcessPackage.ps1 -PackagePath artifacts/packages/CefGlue.Common.1.0.0.nupkg
```

Use the path to the actual package produced by `dotnet pack`. Pack the package without RID or CPU arguments. This check verifies the managed payload is AnyCPU, the runtime configuration specifies net8.0 with Major roll-forward, and the packaged buildTransitive files match this checkout.

## Check Windows consumer output

```powershell
pwsh -NoProfile -File build/Verify-BrowserProcessWindowsOutput.ps1 -Directory artifacts/app -RuntimeIdentifier win-arm64 -MainDirectory artifacts/app -PackagePath artifacts/packages/CefGlue.Common.1.0.0.nupkg
```

Use the actual Build or Publish output paths. `-RuntimeIdentifier` accepts `win-x64` and `win-arm64`. The script checks the BrowserProcess DLL remains AnyCPU, the shared dependencies are AnyCPU or match the requested architecture, and the apphost has that architecture, Windows GUI subsystem, and 8 MiB stack reserve. It also checks that the main output contains exactly one `libcef.dll`, placed alongside the application and matching the requested architecture; extra copies under `runtimes/` fail the check.

`-MainDirectory` also checks the main application's CefGlue dependencies are compatible with the requested architecture. `-PackagePath` compares BrowserProcess files with `tools/browser-process/` and shared dependencies with `lib/net8.0/` in the package by SHA-256; omit it for source ProjectReference consumers.

Both scripts read existing artifacts and exit nonzero when a check fails. They do not build or publish the project. The Windows output check does not execute ARM64 binaries or validate Linux/macOS apphosts.

Build and Publish place the BrowserProcess executable, DLL, deps.json, and runtimeconfig.json alongside the main application. Both processes use the main application's resolved `Xilium.CefGlue.dll` and `Xilium.CefGlue.Common.Shared.dll`. These two shared dependency DLLs remain external when publishing the main application as a single file.
