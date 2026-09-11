using NUnit.Framework;

namespace CefGlue.Tests.Build
{
    [TestFixture]
    public class CefRuntimeAssetTargetTests
    {
        [TestCaseSource(typeof(BrowserProcessTestWorkspace), nameof(BrowserProcessTestWorkspace.RuntimeIdentifiers))]
        public async Task RuntimeSelectionRemovesOnlyCefSdkCopies(string rid)
        {
            var project = Path.Combine(TestRepository.Root, "tests", "CefGlue.Tests", "Build", "Fixtures", "RuntimeAssets.proj");
            var logs = Path.Combine(Path.GetDirectoryName(project)!, "../../../../artifacts/runtime-assets-" + Guid.NewGuid().ToString("N"));
            var command = new BuildTestCommand(Path.GetFullPath(logs));
            await command.RunAsync("select-" + rid, "dotnet", Path.GetDirectoryName(project)!, ["msbuild", project, "-t:Check", "-p:CefGlueTargetRuntimeIdentifier=" + rid, "-nologo", "-verbosity:minimal", "-nr:false"]);
        }
    }
}
