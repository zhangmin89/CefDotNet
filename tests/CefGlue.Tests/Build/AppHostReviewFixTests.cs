using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using NUnit.Framework;

namespace CefGlue.Tests.Build;

[TestFixture, Category("BuildIntegration"), NonParallelizable, Platform("Win")]
public class AppHostReviewFixTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedStackPatchDoesNotPublishAnIncompleteHostAndRetryRepairsIt(bool existingHost)
    {
        var repository = RepositoryRoot();
        var root = Path.GetFullPath(Path.Combine(repository, "artifacts", "apphost-review-" + Guid.NewGuid().ToString("N")));
        Assert.IsFalse(Directory.Exists(root));
        TestContext.Progress.WriteLine($"CREATE {root}");
        Directory.CreateDirectory(root);
        var commands = new BuildTestCommand(Path.Combine(root, "logs"));
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var sdkLog = await commands.RunAsync("sdk-properties", dotnet, repository, ["msbuild", "tests/CefGlue.Tests/CefGlue.Tests.csproj", "-nologo", "-getProperty:MicrosoftNETBuildTasksAssembly,NetCoreRoot"]);
        using var sdk = JsonDocument.Parse(File.ReadAllText(sdkLog));
        var properties = sdk.RootElement.GetProperty("Properties");
        var rid = "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var packs = Path.Combine(properties.GetProperty("NetCoreRoot").GetString()!, "packs", "Microsoft.NETCore.App.Host." + rid);
        var pack = Directory.GetDirectories(packs).Where(path => Version.TryParse(Path.GetFileName(path), out var version) && version.Major == Environment.Version.Major).OrderByDescending(path => Version.Parse(Path.GetFileName(path))).First();
        var apphostSource = Path.Combine(pack, "runtimes", rid, "native", "apphost.exe");
        BrowserProcessAssertions.RequireFile(apphostSource);
        var assembly = Path.Combine(root, "Xilium.CefGlue.BrowserProcess.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Xilium.CefGlue.BrowserProcess.dll"), assembly);
        var source = Path.Combine(repository, "src", "CefGlue.Common", "buildTransitive", "CefGlue.Common.targets");
        var original = XDocument.Load(source).Root!.Elements("Target").Single(target => (string?)target.Attribute("Name") == "CreateCefGlueBrowserProcessAppHost");
        var target = new XElement(original);
        var create = target.Element("CreateAppHost")!;
        var injectedLock = new XElement("ReviewHoldFileLock", new XAttribute("Condition", "'$(ReviewLock)' == 'true'"), new XAttribute("FilePath", create.Attribute("AppHostDestinationPath")!.Value));
        create.AddAfterSelf(injectedLock);
        var lockTask = XElement.Parse("""
            <UsingTask TaskName="ReviewHoldFileLock" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
              <ParameterGroup><FilePath ParameterType="System.String" Required="true" /></ParameterGroup>
              <Task><Using Namespace="System" /><Using Namespace="System.IO" />
                <Code Type="Fragment" Language="cs"><![CDATA[
                  var held = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                  AppDomain.CurrentDomain.SetData("CefGlueReviewLock", held);
                  Log.LogMessage(Microsoft.Build.Framework.MessageImportance.High, "REVIEW_LOCK_ACQUIRED " + FilePath);
                ]]></Code>
              </Task>
            </UsingTask>
            """);
        var intermediate = Path.Combine(root, "obj") + Path.DirectorySeparatorChar;
        var project = new XElement("Project",
            new XElement("PropertyGroup",
                new XElement("OutputType", "Exe"), new XElement("RuntimeIdentifier", rid),
                new XElement("MicrosoftNETBuildTasksAssembly", properties.GetProperty("MicrosoftNETBuildTasksAssembly").GetString()),
                new XElement("AppHostSourcePath", apphostSource), new XElement("CefGlueBrowserProcessAssetsPath", root + Path.DirectorySeparatorChar),
                new XElement("IntermediateOutputPath", intermediate), new XElement("CopyRetryCount", "0"), new XElement("CopyRetryDelayMilliseconds", "0")),
            new XElement("Import", new XAttribute("Project", source)), new XElement("Target", new XAttribute("Name", "ResolveFrameworkReferences")), lockTask, target);
        var fixture = Path.Combine(root, "Probe.proj");
        project.Save(fixture);
        var roundtrip = XDocument.Load(fixture).Root!.Elements("Target").Single(item => (string?)item.Attribute("Name") == "CreateCefGlueBrowserProcessAppHost");
        roundtrip.Element("ReviewHoldFileLock")!.Remove();
        Assert.IsTrue(XNode.DeepEquals(original, roundtrip), "The fixture must use the current product target with only the lock injection added.");
        var host = Path.Combine(intermediate, "cefglue-browser-process", rid, "Xilium.CefGlue.BrowserProcess.exe");
        Task<string> Build(string name, bool locked) => commands.RunAsync(name, dotnet, repository, ["msbuild", fixture, "-t:CreateCefGlueBrowserProcessAppHost", "-nologo", "-m:1", "-nr:false", "-v:normal", "-p:ReviewLock=" + locked.ToString().ToLowerInvariant()]);
        byte[]? originalHost = null;
        if (existingHost)
        {
            await Build("initial", false);
            AssertHost(host);
            originalHost = File.ReadAllBytes(host);
            File.SetLastWriteTimeUtc(assembly, DateTime.UtcNow);
            Assert.Greater(File.GetLastWriteTimeUtc(assembly), File.GetLastWriteTimeUtc(host));
        }
        var failure = Assert.ThrowsAsync<AssertionException>(() => Build("locked", true));
        StringAssert.Contains("PatchPeStackReserve", failure!.Message);
        var failedLog = Directory.GetFiles(Path.Combine(root, "logs"), "*-locked.stdout.log").Single();
        StringAssert.Contains("REVIEW_LOCK_ACQUIRED", File.ReadAllText(failedLog));
        var afterFailure = File.Exists(host) ? File.ReadAllBytes(host) : null;
        await Build("retry", false);
        Assert.Multiple(() =>
        {
            if (existingHost) CollectionAssert.AreEqual(originalHost!, afterFailure!, "Failed rebuilding must preserve the previous complete apphost.");
            else Assert.IsNull(afterFailure, "A failed first build must not publish an apphost.");
            AssertHost(host);
        });
        var timestamp = File.GetLastWriteTimeUtc(host);
        await Build("unchanged", false);
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(host), "An unchanged build must remain incremental.");
        AssertHost(host);
    }

    private static void AssertHost(string path)
    {
        BrowserProcessAssertions.RequireFile(path);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        Assert.AreEqual(8388608, pe.PEHeaders.PEHeader!.SizeOfStackReserve);
        Assert.AreEqual(Subsystem.WindowsGui, pe.PEHeaders.PEHeader.Subsystem);
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", ".."));
}
