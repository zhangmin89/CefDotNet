using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace CefGlue.Tests.Build
{
    [TestFixture]
    public class CefRuntimeAssetTargetTests
    {
        [TestCaseSource(typeof(BrowserProcessTestWorkspace), nameof(BrowserProcessTestWorkspace.RuntimeIdentifiers))]
        public async Task RuntimeSelectionRemovesOnlyCefSdkCopies(string rid)
        {
            var project = GetFixturePath();
            var logs = Path.Combine(Path.GetDirectoryName(project)!, "../../../../artifacts/runtime-assets-" + Guid.NewGuid().ToString("N"));
            var command = new BuildTestCommand(Path.GetFullPath(logs));
            await command.RunAsync("select-" + rid, "dotnet", Path.GetDirectoryName(project)!, ["msbuild", project, "-t:Check", "-p:CefGlueTargetRuntimeIdentifier=" + rid, "-nologo", "-verbosity:minimal", "-nr:false"]);
        }

        private static string GetFixturePath([CallerFilePath] string sourceFile = "") => Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "RuntimeAssets.proj");
    }
}
