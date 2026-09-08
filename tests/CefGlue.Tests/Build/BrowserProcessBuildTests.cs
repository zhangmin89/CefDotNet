using System.IO.Compression;
using System.Text;
using NUnit.Framework;

namespace CefGlue.Tests.Build
{
    [TestFixture]
    [Category("BuildIntegration")]
    [NonParallelizable]
    public class BrowserProcessBuildTests
    {
        private BrowserProcessTestWorkspace _suite = null!;

        [OneTimeSetUp]
        public async Task PackTestPackage()
        {
            _suite = await BrowserProcessTestWorkspace.CreateAsync();
            await _suite.PackAsync(_suite.Feed);
        }

        [Test]
        public void NeutralPackageContainsOnlyPortableManagedAssets()
        {
            BrowserProcessAssertions.PackageIsPortable(_suite.PackagePath, _suite.RepositoryRoot);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ConsumerBuildCreatesMatchingHelperAndCompleteNativeAssets(bool sourceReference)
        {
            var workspace = _suite.CreateCase(nameof(ConsumerBuildCreatesMatchingHelperAndCompleteNativeAssets));
            var project = workspace.CreateConsumer(sourceReference);
            await workspace.DotnetAsync("default-build", "build", project);
            ValidateOutput(workspace, workspace.BuildOutput(), workspace.NativeRid, sourceReference);
            await workspace.StartHelperAsync(workspace.BuildOutput());
        }

        [TestCase(false, "default")]
        [TestCase(false, "no-main-apphost")]
        [TestCase(false, "single-file")]
        [TestCase(true, "native")]
        public async Task ConsumerPublishKeepsHelperIndependentlyExecutable(bool sourceReference, string mode)
        {
            var workspace = _suite.CreateCase(nameof(ConsumerPublishKeepsHelperIndependentlyExecutable));
            var project = workspace.CreateConsumer(sourceReference);
            var output = Path.Combine(workspace.Root, "publish");
            var arguments = new List<string> { "publish", project, "-o", output };
            if (mode != "default") { arguments.AddRange(["-r", workspace.NativeRid, "--self-contained", "false"]); }
            if (mode == "no-main-apphost") { arguments.Add("-p:UseAppHost=false"); }
            if (mode == "single-file") { arguments.Add("-p:PublishSingleFile=true"); }
            await workspace.DotnetAsync("publish-" + mode, arguments.ToArray());
            ValidateOutput(workspace, output, workspace.NativeRid, sourceReference, singleFile: mode == "single-file");
            var mainHost = Path.Combine(output, "Consumer" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (mode == "no-main-apphost") { Assert.IsFalse(File.Exists(mainHost), "UseAppHost=false still produced the main apphost."); }
            if (mode == "single-file")
            {
                BrowserProcessAssertions.RequireFile(mainHost);
                Assert.IsFalse(File.Exists(Path.Combine(output, "Consumer.dll")), "The main application was not bundled.");
            }
            await workspace.StartHelperAsync(output);
        }

        [TestCase("unchanged")]
        [TestCase("missing-host")]
        [TestCase("changed-input")]
        public async Task IncrementalBuildRegeneratesHelperOnlyWhenRequired(string change)
        {
            var workspace = _suite.CreateCase(nameof(IncrementalBuildRegeneratesHelperOnlyWhenRequired));
            var project = workspace.CreateConsumer(sourceReference: false);
            await workspace.DotnetAsync("initial-build", "build", project);
            ValidateOutput(workspace, workspace.BuildOutput(), workspace.NativeRid);
            var hosts = Directory.GetFiles(Path.Combine(workspace.Work, "obj", "Consumer"), workspace.HostName, SearchOption.AllDirectories);
            Assert.AreEqual(1, hosts.Length, "Expected one generated helper apphost.");
            var host = hosts[0];
            var timestamp = File.GetLastWriteTimeUtc(host);
            if (change == "missing-host")
            {
                var saved = Path.Combine(workspace.Root, "removed-apphost.bin");
                TestContext.Progress.WriteLine($"MOVE {host} -> {saved}");
                File.Move(host, saved);
            }
            if (change == "changed-input")
            {
                TestContext.Progress.WriteLine($"APPEND UTF8 {project}");
                File.AppendAllText(project, "\n<!-- Changed build input for incremental regression. -->\n", new UTF8Encoding(false, true));
            }
            await workspace.DotnetAsync("rebuild-" + change, "build", project, "--no-restore");
            BrowserProcessAssertions.RequireFile(host);
            if (change == "unchanged") { Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(host), "Unchanged build regenerated the helper apphost."); }
            else { Assert.Greater(File.GetLastWriteTimeUtc(host), timestamp, "Missing host or changed build input did not regenerate the helper apphost."); }
            ValidateOutput(workspace, workspace.BuildOutput(), workspace.NativeRid);
        }

        [Test]
        [Platform("Win")]
        public async Task SourceArchitectureSwitchDoesNotContaminateNeutralPackage()
        {
            var workspace = _suite.CreateCase(nameof(SourceArchitectureSwitchDoesNotContaminateNeutralPackage));
            var project = workspace.CreateConsumer(sourceReference: true);
            foreach (var architecture in new[] { "arm64", "x64" })
            {
                var rid = "win-" + architecture;
                await workspace.DotnetAsync("source-build-" + architecture, "build", project, "-r", rid, $"-p:Platform={architecture}", $"-p:PlatformTarget={architecture}");
                ValidateOutput(workspace, workspace.BuildOutput(rid), rid, sourceReference: true);
            }
            // Reuse the build workspace, but pass no RID, CPU or publish properties to pack.
            var package = await workspace.PackAsync(Path.Combine(workspace.Root, "repacked"));
            BrowserProcessAssertions.PackageIsPortable(package, workspace.RepositoryRoot);
        }

        [Test]
        [Platform("Win")]
        public async Task PlatformTargetSwitchRefreshesAllNativeAssetsInSameBuildDirectory()
        {
            var workspace = _suite.CreateCase(nameof(PlatformTargetSwitchRefreshesAllNativeAssetsInSameBuildDirectory));
            var project = workspace.CreateConsumer(sourceReference: false);
            foreach (var architecture in new[] { "x64", "ARM64", "x64" })
            {
                await workspace.DotnetAsync("build-" + architecture, "build", project, $"-p:PlatformTarget={architecture}");
                ValidateOutput(workspace, workspace.BuildOutput(), "win-" + architecture.ToLowerInvariant());
            }
        }

        [Test]
        [Platform("Win")]
        public async Task ExplicitArm64RidCreatesArm64HelperAndNativeAssets()
        {
            var workspace = _suite.CreateCase(nameof(ExplicitArm64RidCreatesArm64HelperAndNativeAssets));
            var project = workspace.CreateConsumer(sourceReference: false);
            await workspace.DotnetAsync("arm64-build", "build", project, "-r", "win-arm64");
            ValidateOutput(workspace, workspace.BuildOutput("win-arm64"), "win-arm64");
        }

        [Test]
        [Platform("Win")]
        public async Task RidSwitchRefreshesAllNativeAssetsInSamePublishDirectory()
        {
            var workspace = _suite.CreateCase(nameof(RidSwitchRefreshesAllNativeAssetsInSamePublishDirectory));
            var project = workspace.CreateConsumer(sourceReference: false);
            var output = Path.Combine(workspace.Root, "publish");
            foreach (var rid in new[] { "win-x64", "win-arm64", "win-x64" })
            {
                await workspace.DotnetAsync("publish-" + rid, "publish", project, "-r", rid, "--self-contained", "false", "-o", output);
                ValidateOutput(workspace, output, rid);
            }
        }

        [Test]
        public async Task NativeResourceValidationRejectsMissingResource()
        {
            var workspace = _suite.CreateCase(nameof(NativeResourceValidationRejectsMissingResource));
            var project = workspace.CreateConsumer(sourceReference: false);
            await workspace.DotnetAsync("initial-build", "build", project);
            var output = workspace.BuildOutput();
            ValidateOutput(workspace, output, workspace.NativeRid);
            var resources = Directory.GetFiles(output, "*.pak", SearchOption.AllDirectories);
            Assert.IsNotEmpty(resources, "CEF output must contain resources.");
            var resource = resources[0];
            var saved = Path.Combine(workspace.Root, "omitted-resource.pak");
            TestContext.Progress.WriteLine($"MOVE {resource} -> {saved}");
            File.Move(resource, saved);
            try
            {
                var error = Assert.Throws<AssertionException>(() => BrowserProcessAssertions.NativeResources(output, workspace.NativeRid, workspace.AssetsFile));
                StringAssert.Contains("Missing CEF resource:", error!.Message);
            }
            finally
            {
                TestContext.Progress.WriteLine($"RESTORE {saved} -> {resource}");
                File.Move(saved, resource);
            }
            ValidateOutput(workspace, output, workspace.NativeRid);
        }

        [TestCase("arm64-library")]
        [TestCase("missing-deps")]
        public void PackageValidationRejectsInvalidPayload(string mutation)
        {
            var workspace = _suite.CreateCase(nameof(PackageValidationRejectsInvalidPayload));
            var mutatedPackage = Path.Combine(workspace.Root, mutation + ".nupkg");
            File.Copy(_suite.PackagePath, mutatedPackage);
            using (var archive = ZipFile.Open(mutatedPackage, ZipArchiveMode.Update))
            {
                if (mutation == "missing-deps")
                {
                    archive.GetEntry("tools/browser-process/Xilium.CefGlue.BrowserProcess.deps.json")!.Delete();
                }
                else
                {
                    var entry = archive.GetEntry("lib/net8.0/Xilium.CefGlue.dll")!;
                    using var buffer = new MemoryStream();
                    using (var stream = entry.Open()) { stream.CopyTo(buffer); }
                    var bytes = buffer.ToArray();
                    var offset = BitConverter.ToInt32(bytes, 0x3c) + 4;
                    BitConverter.GetBytes((ushort)0xaa64).CopyTo(bytes, offset);
                    entry.Delete();
                    using var output = archive.CreateEntry("lib/net8.0/Xilium.CefGlue.dll").Open();
                    output.Write(bytes);
                }
            }
            var error = Assert.Throws<AssertionException>(() => BrowserProcessAssertions.PackageIsPortable(mutatedPackage, _suite.RepositoryRoot));
            StringAssert.Contains(mutation == "arm64-library" ? "Payload is not AnyCPU: lib/net8.0/Xilium.CefGlue.dll" : "Missing package payload: tools/browser-process/Xilium.CefGlue.BrowserProcess.deps.json", error!.Message);
        }

        private static void ValidateOutput(BrowserProcessTestWorkspace workspace, string directory, string rid, bool sourceReference = false, bool singleFile = false)
        {
            BrowserProcessAssertions.ConsumerOutput(directory, rid, sourceReference ? null : workspace.PackagePath, singleFile);
            BrowserProcessAssertions.NativeResources(directory, rid, workspace.AssetsFile);
        }
    }
}
