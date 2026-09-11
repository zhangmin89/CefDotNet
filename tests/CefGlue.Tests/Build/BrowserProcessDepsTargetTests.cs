using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using NUnit.Framework;

namespace CefGlue.Tests.Build
{
    [TestFixture]
    public class BrowserProcessDepsTargetTests
    {
        [TestCaseSource(typeof(BrowserProcessTestWorkspace), nameof(BrowserProcessTestWorkspace.RuntimeIdentifiers))]
        public async Task DepsTaskPreservesManagedDependenciesAndAddsRuntimePack(string rid)
        {
            var repository = TestRepository.Root;
            var root = Path.Combine(repository, "artifacts", "browser-process-deps-" + Guid.NewGuid().ToString("N"));
            Assert.IsFalse(Directory.Exists(root));
            TestContext.Progress.WriteLine($"CREATE {root}");
            Directory.CreateDirectory(root);
            var source = Path.Combine(root, "source.deps.json");
            var destination = Path.Combine(root, "output.deps.json");
            var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Xilium.CefGlue.BrowserProcess.deps.json"), new UTF8Encoding(false, true));
            File.WriteAllText(source, original, new UTF8Encoding(false, true));
            const string Framework = ".NETCoreApp,Version=v8.0";
            const string Version = "8.0.28";
            var package = "Microsoft.NETCore.App.Runtime." + rid;
            var library = "runtimepack." + package;
            var libraryKey = library + "/" + Version;
            var fixture = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("Import", new XAttribute("Project", Path.Combine(repository, "src", "CefGlue.Common", "buildTransitive", "CefGlue.Common.targets"))),
                new XElement("ItemGroup", Asset("runtime", "System.Private.CoreLib.dll"), Asset("native", "native/runtime.bin")),
                new XElement("Target", new XAttribute("Name", "Generate"),
                    new XElement("GenerateCefGlueBrowserProcessDeps", new XAttribute("SourcePath", source), new XAttribute("DestinationPath", destination), new XAttribute("TargetFrameworkMoniker", Framework), new XAttribute("RuntimeIdentifier", rid), new XAttribute("RuntimePackAssets", "@(RuntimePackAsset)"))));
            var project = Path.Combine(root, "Probe.proj");
            fixture.Save(project);
            var command = new BuildTestCommand(Path.Combine(root, "logs"));
            await command.RunAsync("generate-deps", "dotnet", repository, ["msbuild", project, "-t:Generate", "-nologo", "-verbosity:minimal", "-nr:false"]);
            BrowserProcessAssertions.RequireFile(destination);
            var actual = JsonNode.Parse(File.ReadAllText(destination))!;
            var expected = JsonNode.Parse(original)!;
            var targetName = Framework + "/" + rid;
            var target = expected["targets"]!.AsObject().Single().Value!.DeepClone();
            var helper = target.AsObject().Single(item => item.Key.StartsWith("Xilium.CefGlue.BrowserProcess/", StringComparison.Ordinal)).Value!;
            helper["dependencies"]![library] = Version;
            target[libraryKey] = new JsonObject { ["runtime"] = new JsonObject { ["System.Private.CoreLib.dll"] = new JsonObject() }, ["native"] = new JsonObject { ["native/runtime.bin"] = new JsonObject() } };
            expected["targets"] = new JsonObject { [targetName] = target };
            expected["runtimeTarget"]!["name"] = targetName;
            expected["libraries"]![libraryKey] = new JsonObject { ["type"] = "runtimepack", ["serviceable"] = false, ["sha512"] = "" };
            Assert.IsTrue(JsonNode.DeepEquals(expected, actual), "Generated deps must preserve the helper's managed graph and add the selected runtime pack with portable asset paths.");
            Assert.AreEqual(original, File.ReadAllText(source), "Generating RID-specific deps must not mutate the neutral input.");

            XElement Asset(string type, string path) => new("RuntimePackAsset", new XAttribute("Include", path), new XElement("NuGetPackageId", package), new XElement("NuGetPackageVersion", Version), new XElement("AssetType", type), new XElement("DestinationSubPath", path));
        }
    }
}
