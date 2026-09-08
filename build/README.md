# BrowserProcess build verification

BrowserProcess build/package regression tests live in the existing NUnit project under `tests/CefGlue.Tests/Build/`. Run them with `dotnet test`; no PowerShell test runner, additional project, or additional dependency is needed. Generated packages, consumer outputs, logs, and temporary fixtures belong in `artifacts/`.

`CefGlue.Package.targets` packages the managed BrowserProcess assets. Consumer Build and Publish generate the native apphost through `src/CefGlue.Common/buildTransitive/CefGlue.Common.targets`.

## Run the build and package regression suite

```powershell
dotnet test tests/CefGlue.Tests/CefGlue.Tests.csproj -c Release --filter TestCategory=BuildIntegration --logger 'trx;LogFileName=build-integration.trx' --results-directory artifacts/test-results --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type none
```

`BrowserProcessBuildTests` contains 17 NUnit cases on Windows. The fixture is non-parallel and each case has its own consumer and build outputs; tests do not depend on execution order. A run creates a new `artifacts/browser-process-nunit-*` directory and shares only its local package feed/cache between cases. It uses the repository's normal NuGet sources and preserves audit and warning policies. Prior run directories are retained.

The suite covers:

- Configured repository source references and NuGet `PackageReference` consumers, including ordinary Build and Publish with the helper alongside the main application.
- Windows ARM64 → x64 builds in the same artifacts workspace, followed by ordinary RID/CPU-neutral `pack`; both `tools/browser-process/` and `lib/net8.0/` must remain AnyCPU in the package.
- Build without a RID, Windows `PlatformTarget` switching x64 → ARM64 → x64 in the same output directory, explicit `win-arm64` builds, and x64 → ARM64 → x64 publishing into the same publish directory. All native files and the helper must match the selected architecture even when the newly selected package contains older files.
- An unchanged incremental build, regeneration after the generated host is moved away, and regeneration after a project input changes.
- `UseAppHost=false` and framework-dependent `PublishSingleFile=true`, with the helper and its shared dependencies remaining external and independently executable.
- Every selected CEF resource and locale matching the resolved native package by SHA-256, no duplicate CEF files under `runtimes/`, and preservation of an unrelated native asset supplied by a tiny local test package.
- Negative checks that reject an ARM64 library in the neutral package, missing helper deps.json, and an omitted CEF resource. Mutations affect only temporary package/output copies.
- Standalone helper startup for native Build and Publish output. The existing real-browser `JavascriptEvaluationTests.NumberReturn` integration test runs separately, using the command below.

External plain `ProjectReference` is not a supported scenario and is not tested. The source fixture explicitly uses the repository's `CefGlueUseLocalRuntimeAssets` integration. There is no new production project and no self-contained or trimming test configuration.

`.github/workflows/ci.yml` runs these tests on Windows x64, Linux x64/ARM64, and macOS x64/ARM64. The four Windows-specific architecture-switch/RID cases use NUnit's platform attribute; the remaining cases run on every matrix host. Windows x64 inspects ARM64 output without executing those binaries. Native startup and browser tests run on the current OS/CPU. Linux needs Xvfb and the CEF system libraries installed by the CI job; prefix the test commands with `xvfb-run --auto-servernum`. Missing Unix resources and failing browser startup are failures.

The tests report individual results through NUnit/TRX. `BuildTestCommand` records each child command's arguments, PID, working directory, exit code, stdout, and stderr, and adds these files as test attachments. It records process/log progress at 60-second intervals for builds and 10-second intervals for helper startup, and stops a command after two intervals without progress. This is build integration testing: NUnit invokes `dotnet pack/build/publish`, then checks the resulting files using .NET APIs. It never launches another `dotnet test` from inside a test.

## Run ordinary tests and the real-browser smoke test

```powershell
dotnet test -c Release --filter 'TestCategory!=BuildIntegration' --blame-hang --blame-hang-timeout 2m --blame-hang-dump-type none

dotnet test tests/CefGlue.Avalonia.Tests/CefGlue.Avalonia.Tests.csproj -c Release --filter FullyQualifiedName=CefGlue.Tests.Javascript.JavascriptEvaluationTests.NumberReturn --logger 'trx;LogFileName=browser-smoke.trx' --results-directory artifacts/test-results --blame-hang --blame-hang-timeout 2m --blame-hang-dump-type none
```

An unfiltered `dotnet test` includes the build integration fixture. CI filters it out of the ordinary Windows test job because the matrix job runs it separately. The browser smoke command selects exactly the existing `NumberReturn` test; check its TRX for one passing result when running it independently.

## Assertions and output layout

`BrowserProcessAssertions` checks required package payload names, AnyCPU DLLs in both `tools/browser-process/` and `lib/`, a RID-neutral deps.json, net8.0 with Major roll-forward, no native host/private runtime, and packaged buildTransitive files matching this checkout. PDBs are optional; the assertions do not depend on a fixed file count.

Consumer checks inspect Windows PE architecture, GUI subsystem, and 8 MiB apphost stack reserve, or Unix ELF/Mach-O architecture and executable permission. Managed helper and shared dependency hashes are compared with the package for package consumers. Every native CEF resource and locale is compared with the consumer's resolved NuGet package inventory, including the files affected by switching architectures in an existing output directory.

Build and Publish place the BrowserProcess executable, DLL, deps.json, and runtimeconfig.json alongside the main application. Both processes use the main application's resolved `Xilium.CefGlue.dll` and `Xilium.CefGlue.Common.Shared.dll`. These two shared dependency DLLs remain external when publishing the main application as a single file.
