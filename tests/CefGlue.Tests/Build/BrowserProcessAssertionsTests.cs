using System.Text.Json;
using NUnit.Framework;

namespace CefGlue.Tests.Build
{
    [TestFixture]
    [NonParallelizable]
    public class BrowserProcessAssertionsTests
    {
        [Test]
        public void NativeResourceValidationReusesTheSourceHash()
        {
            var (source, output, assets) = CreateNativeResources();
            BrowserProcessAssertions.NativeResources(output, "linux-x64", assets);
            using var lockedSource = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.None);
            BrowserProcessAssertions.NativeResources(output, "linux-x64", assets);
        }

        [Test]
        public void NativeResourceValidationDetectsChangedTargetWithUnchangedMetadata()
        {
            var (_, output, assets) = CreateNativeResources();
            BrowserProcessAssertions.NativeResources(output, "linux-x64", assets);
            var target = Path.Combine(output, "resource.pak");
            var modified = File.GetLastWriteTimeUtc(target);
            File.WriteAllBytes(target, [4, 3, 2, 1]);
            File.SetLastWriteTimeUtc(target, modified);
            var error = Assert.Throws<AssertionException>(() => BrowserProcessAssertions.NativeResources(output, "linux-x64", assets));
            StringAssert.Contains("CEF resource differs", error!.Message);
        }

        [TestCase("length")]
        [TestCase("timestamp")]
        public void NativeResourceValidationRefreshesChangedSourceHash(string change)
        {
            var (source, output, assets) = CreateNativeResources();
            BrowserProcessAssertions.NativeResources(output, "linux-x64", assets);
            var modified = File.GetLastWriteTimeUtc(source);
            File.WriteAllBytes(source, change == "length" ? [1, 2, 3, 4, 5] : [4, 3, 2, 1]);
            File.SetLastWriteTimeUtc(source, change == "length" ? modified : modified.AddSeconds(2));
            File.Copy(source, Path.Combine(output, "resource.pak"), overwrite: true);
            BrowserProcessAssertions.NativeResources(output, "linux-x64", assets);
        }

        private static (string Source, string Output, string Assets) CreateNativeResources()
        {
            var root = Path.Combine(TestRepository.Root, "artifacts", "native-hash-tests-" + Guid.NewGuid().ToString("N"));
            var packages = Path.Combine(root, "packages");
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            var ids = new[] { "chromiumembeddedframework.runtime.win-x64", "chromiumembeddedframework.runtime.win-arm64", "cef.redist.linux64", "cef.redist.linuxarm64", "cef.redist.osx64", "cef.redist.osx.arm64" };
            foreach (var id in ids) { Directory.CreateDirectory(Path.Combine(packages, id)); }
            var source = Path.Combine(packages, "cef.redist.linux64", "CEF", "resource.pak");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, [1, 2, 3, 4]);
            File.Copy(source, Path.Combine(output, "resource.pak"));
            File.WriteAllText(Path.Combine(output, "cefglue-test-native-linux-x64.bin"), "sentinel:linux-x64");
            var assets = Path.Combine(root, "project.assets.json");
            File.WriteAllText(assets, JsonSerializer.Serialize(new { libraries = ids.ToDictionary(id => id + "/1.0.0", id => new { path = id }), packageFolders = new Dictionary<string, object> { [packages] = new { } } }));
            return (source, output, assets);
        }
    }
}
