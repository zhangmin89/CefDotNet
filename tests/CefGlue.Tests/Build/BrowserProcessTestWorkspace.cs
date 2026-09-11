using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using NUnit.Framework;

namespace CefGlue.Tests.Build
{
    internal sealed class BrowserProcessTestWorkspace
    {
        internal static readonly string[] RuntimeIdentifiers = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly string _dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        private readonly BuildTestCommand _commands;
        private string _globalPackages = "";

        private BrowserProcessTestWorkspace(string repositoryRoot, string root, string feed, string packages, string version)
        {
            RepositoryRoot = repositoryRoot;
            Root = root;
            Feed = feed;
            Packages = packages;
            Version = version;
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : throw new PlatformNotSupportedException();
            NativeRid = os + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            Assert.Contains(NativeRid, RuntimeIdentifiers);
            Assert.IsFalse(Directory.Exists(root), $"Refusing to overwrite an earlier run: {root}");
            Directory.CreateDirectory(root);
            _commands = new BuildTestCommand(Path.Combine(root, "logs"));
            TestContext.Progress.WriteLine($"ARTIFACTS {root}");
        }

        public string RepositoryRoot { get; }
        public string Root { get; }
        public string Feed { get; }
        public string Packages { get; }
        public string Version { get; }
        public string NativeRid { get; }
        public string Work => Path.Combine(Root, "work");
        public string PackagePath => Path.Combine(Feed, $"CefGlue.Common.{Version}.nupkg");
        public string HostName => "Xilium.CefGlue.BrowserProcess" + (NativeRid.StartsWith("win-") ? ".exe" : "");

        public static async Task<BrowserProcessTestWorkspace> CreateAsync()
        {
            var repo = TestRepository.Root;
            Assert.IsTrue(File.Exists(Path.Combine(repo, "CefDotNet.slnx")), $"Tests require a repository checkout: {repo}");
            var root = Path.Combine(repo, "artifacts", "browser-process-nunit-" + Guid.NewGuid().ToString("N"));
            var workspace = new BrowserProcessTestWorkspace(repo, root, Path.Combine(root, "feed"), Path.Combine(root, "packages"), "0.0.0-browserprocess." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace.Feed);
            var cacheLog = await workspace._commands.RunAsync("nuget-cache", workspace._dotnet, repo, ["nuget", "locals", "global-packages", "--list"]);
            const string Prefix = "global-packages: ";
            workspace._globalPackages = File.ReadLines(cacheLog).Single(line => line.StartsWith(Prefix))[Prefix.Length..].Trim();
            workspace.CreateNativeSentinelPackage();
            return workspace;
        }

        public BrowserProcessTestWorkspace CreateCase(string name)
        {
            return new BrowserProcessTestWorkspace(RepositoryRoot, Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")), Feed, Packages, Version) { _globalPackages = _globalPackages };
        }

        public string WriteFile(string relativePath, string content)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relativePath));
            Assert.IsTrue(path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal), $"Path outside test workspace: {path}");
            Assert.IsFalse(File.Exists(path), $"Refusing to overwrite: {path}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Utf8);
            return path;
        }

        public string CreateConsumer(bool sourceReference)
        {
            WriteFile("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>");
            var repo = SecurityElement.Escape(RepositoryRoot.Replace('\\', '/'));
            WriteFile("Directory.Build.targets", $"<Project><Import Project=\"{repo}/Directory.Build.targets\" Condition=\"'$(CefGlueUseLocalRuntimeAssets)' == 'true'\" /></Project>");
            WriteFile("Directory.Packages.props", "<Project />");
            var references = sourceReference
                ? $"<ProjectReference Include=\"{repo}/src/CefGlue.Common/CefGlue.Common.csproj\" /><ProjectReference Include=\"{repo}/src/CefGlue/CefGlue.csproj\" />"
                : $"<PackageReference Include=\"CefGlue.Common\" Version=\"{Version}\" />";
            var localAssets = sourceReference ? "<CefGlueUseLocalRuntimeAssets>true</CefGlueUseLocalRuntimeAssets>" : "";
            WriteFile("consumer/Program.cs", "Console.WriteLine(typeof(Xilium.CefGlue.Common.CefRuntimeLoader).Assembly.GetName().Name); Console.WriteLine(typeof(Xilium.CefGlue.CefRuntime).Assembly.GetName().Name);");
            return WriteFile("consumer/Consumer.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>{localAssets}</PropertyGroup><ItemGroup>{references}<PackageReference Include=\"CefGlue.Tests.NativeSentinel\" Version=\"{Version}\" /></ItemGroup></Project>");
        }

        public async Task<string> PackAsync(string outputDirectory)
        {
            await DotnetAsync("neutral-pack", "pack", Path.Combine(RepositoryRoot, "src/CefGlue.Common/CefGlue.Common.csproj"), $"-p:PackageVersion={Version}", "-o", outputDirectory);
            var package = Path.Combine(outputDirectory, $"CefGlue.Common.{Version}.nupkg");
            BrowserProcessAssertions.RequireFile(package);
            return package;
        }

        public Task<string> DotnetAsync(string name, params string[] arguments)
        {
            var common = new List<string> { "-c", "Release", "-nologo", "-verbosity:minimal", "-m:1", "-nr:false", "-p:UseSharedCompilation=false", $"-p:ArtifactsPath={Work}", $"-p:RestoreAdditionalProjectSources={Feed}", $"-p:RestorePackagesPath={Packages}" };
            if (Directory.Exists(_globalPackages)) { common.Add($"-p:RestoreFallbackFolders={_globalPackages}"); }
            return _commands.RunAsync(name, _dotnet, RepositoryRoot, arguments.Concat(common));
        }

        public Task<string> StartHelperAsync(string directory)
        {
            return _commands.RunAsync("helper-startup", Path.Combine(directory, HostName), directory, [], startup: true);
        }

        public Task<string> StartConsumerAsync(string directory)
        {
            return _commands.RunAsync("consumer-startup", Path.Combine(directory, "Consumer" + (OperatingSystem.IsWindows() ? ".exe" : "")), directory, [], startup: true);
        }

        public string BuildOutput(string? rid = null) => Path.Combine(Work, "bin", "Consumer", rid == null ? "release" : "release_" + rid);
        public string AssetsFile => Path.Combine(Work, "obj", "Consumer", "project.assets.json");

        private void CreateNativeSentinelPackage()
        {
            using var archive = ZipFile.Open(Path.Combine(Feed, $"CefGlue.Tests.NativeSentinel.{Version}.nupkg"), ZipArchiveMode.Create);
            using (var writer = new StreamWriter(archive.CreateEntry("CefGlue.Tests.NativeSentinel.nuspec").Open(), Utf8))
            {
                writer.Write($"<package><metadata><id>CefGlue.Tests.NativeSentinel</id><version>{Version}</version><authors>CefGlue tests</authors><description>Native copy regression fixture</description></metadata></package>");
            }
            foreach (var rid in RuntimeIdentifiers)
            {
                using var writer = new StreamWriter(archive.CreateEntry($"runtimes/{rid}/native/cefglue-test-native-{rid}.bin").Open(), Utf8);
                writer.Write($"sentinel:{rid}");
            }
        }
    }
}
