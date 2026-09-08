using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace CefGlue.Tests.Build
{
    internal static class BrowserProcessAssertions
    {
        private static readonly string[] ManagedNames = ["Xilium.CefGlue.BrowserProcess", "Xilium.CefGlue.Common.Shared", "Xilium.CefGlue"];
        private static readonly string[] PayloadNames = [.. ManagedNames.Select(name => name + ".dll"), "Xilium.CefGlue.BrowserProcess.deps.json", "Xilium.CefGlue.BrowserProcess.runtimeconfig.json"];

        public static void RequireFile(string path)
        {
            Assert.IsTrue(File.Exists(path), $"Missing file: {path}");
            Assert.Greater(new FileInfo(path).Length, 0, $"Empty file: {path}");
        }

        public static void PackageIsPortable(string packagePath, string repositoryRoot)
        {
            using var package = ZipFile.OpenRead(packagePath);
            foreach (var name in PayloadNames) { RequiredEntry(package, "tools/browser-process/" + name); }
            foreach (var name in new[] { "Xilium.CefGlue.Common", "Xilium.CefGlue.Common.Shared", "Xilium.CefGlue" }) { RequiredEntry(package, $"lib/net8.0/{name}.dll"); }
            var allowedPayload = PayloadNames.Concat(ManagedNames.Select(name => name + ".pdb")).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in package.Entries)
            {
                if (entry.FullName.StartsWith("tools/browser-process/"))
                {
                    Assert.IsTrue(allowedPayload.Contains(entry.FullName["tools/browser-process/".Length..]), $"Unexpected payload: {entry.FullName}");
                }
                if ((entry.FullName.StartsWith("tools/browser-process/") || entry.FullName.StartsWith("lib/")) && entry.FullName.EndsWith(".dll"))
                {
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    buffer.Position = 0;
                    using var pe = new PEReader(buffer);
                    Assert.IsTrue(IsAnyCpu(pe), $"Payload is not AnyCPU: {entry.FullName}");
                }
                Assert.IsFalse(Regex.IsMatch(entry.FullName, @"(^|/)(hostfxr|hostpolicy|coreclr|libhostfxr|libhostpolicy|libcoreclr|System\.Private\.CoreLib)\.") || entry.FullName.EndsWith(".exe") || entry.FullName.StartsWith("runtimes/"), $"Package contains a native host or private runtime: {entry.FullName}");
            }
            using (var stream = RequiredEntry(package, "tools/browser-process/Xilium.CefGlue.BrowserProcess.runtimeconfig.json").Open()) { RuntimeIsFrameworkDependent(stream); }
            using (var stream = RequiredEntry(package, "tools/browser-process/Xilium.CefGlue.BrowserProcess.deps.json").Open())
            using (var dependencies = JsonDocument.Parse(stream))
            {
                Assert.AreEqual(".NETCoreApp,Version=v8.0", dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString(), "BrowserProcess deps.json must be RID-neutral.");
            }
            foreach (var extension in new[] { "props", "targets" })
            {
                using var stream = RequiredEntry(package, "buildTransitive/CefGlue.Common." + extension).Open();
                Assert.AreEqual(HashFile(Path.Combine(repositoryRoot, "src/CefGlue.Common/buildTransitive/CefGlue.Common." + extension)), Hash(stream), $"Packaged .{extension} differs from source.");
            }
        }

        public static void ConsumerOutput(string directory, string rid, string? packagePath, bool singleFile = false)
        {
            foreach (var name in PayloadNames) { RequireFile(Path.Combine(directory, name)); }
            var expectedMachine = rid.EndsWith("arm64") ? Machine.Arm64 : Machine.Amd64;
            var managedNames = singleFile ? ManagedNames : ManagedNames.Append("Xilium.CefGlue.Common");
            foreach (var name in managedNames)
            {
                var path = Path.Combine(directory, name + ".dll");
                RequireFile(path);
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                Assert.IsNotNull(pe.PEHeaders.CorHeader, $"Not a managed assembly: {path}");
                var portableRequired = name == "Xilium.CefGlue.BrowserProcess" || !rid.StartsWith("win-");
                Assert.IsTrue(IsAnyCpu(pe) || (!portableRequired && pe.PEHeaders.CoffHeader.Machine == expectedMachine), $"Managed dependency architecture mismatch: {path}; expected {rid}");
            }
            using (var stream = File.OpenRead(Path.Combine(directory, "Xilium.CefGlue.BrowserProcess.runtimeconfig.json"))) { RuntimeIsFrameworkDependent(stream); }

            var hostPath = Path.Combine(directory, "Xilium.CefGlue.BrowserProcess" + (rid.StartsWith("win-") ? ".exe" : ""));
            RequireFile(hostPath);
            using (var stream = File.OpenRead(hostPath))
            {
                if (rid.StartsWith("win-"))
                {
                    using var pe = new PEReader(stream);
                    Assert.AreEqual(expectedMachine, pe.PEHeaders.CoffHeader.Machine, "Incorrect apphost architecture.");
                    Assert.AreEqual(8388608, pe.PEHeaders.PEHeader!.SizeOfStackReserve, "Apphost stack reserve must be 8 MiB.");
                    Assert.AreEqual(Subsystem.WindowsGui, pe.PEHeaders.PEHeader.Subsystem);
                }
                else
                {
                    var header = new byte[32];
                    stream.ReadExactly(header);
                    if (rid.StartsWith("linux-"))
                    {
                        Assert.AreEqual(0x464c457fu, BitConverter.ToUInt32(header, 0), "Apphost must be ELF.");
                        Assert.AreEqual(2, header[4], "Apphost must be 64-bit.");
                        Assert.AreEqual(1, header[5], "Apphost must be little-endian.");
                        Assert.AreEqual(rid.EndsWith("arm64") ? 183 : 62, BitConverter.ToUInt16(header, 18));
                    }
                    else
                    {
                        Assert.AreEqual(0xfeedfacfu, BitConverter.ToUInt32(header, 0), "Apphost must be Mach-O.");
                        Assert.AreEqual(rid.EndsWith("arm64") ? 0x0100000cu : 0x01000007u, BitConverter.ToUInt32(header, 4));
                    }
                    if (!OperatingSystem.IsWindows()) { Assert.IsTrue((File.GetUnixFileMode(hostPath) & UnixFileMode.UserExecute) != 0, "Apphost must be executable."); }
                }
            }
            if (rid.StartsWith("win-"))
            {
                var cefPath = Path.Combine(directory, "libcef.dll");
                CollectionAssert.AreEqual(new[] { cefPath }, Directory.GetFiles(directory, "libcef.dll", SearchOption.AllDirectories), "Expected one libcef.dll alongside the application.");
                using var stream = File.OpenRead(cefPath);
                using var pe = new PEReader(stream);
                Assert.AreEqual(expectedMachine, pe.PEHeaders.CoffHeader.Machine, "Incorrect CEF architecture.");
            }
            if (packagePath != null)
            {
                using var package = ZipFile.OpenRead(packagePath);
                foreach (var name in PayloadNames)
                {
                    var prefix = name is "Xilium.CefGlue.Common.Shared.dll" or "Xilium.CefGlue.dll" ? "lib/net8.0/" : "tools/browser-process/";
                    using var stream = RequiredEntry(package, prefix + name).Open();
                    Assert.AreEqual(Hash(stream), HashFile(Path.Combine(directory, name)), $"Consumer payload differs from package: {name}");
                }
            }
        }

        public static void NativeResources(string directory, string rid, string assetsFile)
        {
            using var assets = JsonDocument.Parse(File.ReadAllText(assetsFile));
            var packages = new Dictionary<string, string>
            {
                ["win-x64"] = "chromiumembeddedframework.runtime.win-x64",
                ["win-arm64"] = "chromiumembeddedframework.runtime.win-arm64",
                ["linux-x64"] = "cef.redist.linux64",
                ["linux-arm64"] = "cef.redist.linuxarm64",
                ["osx-x64"] = "cef.redist.osx64",
                ["osx-arm64"] = "cef.redist.osx.arm64"
            }.ToDictionary(pair => pair.Key, pair => PackageDirectory(assets.RootElement, pair.Value));
            var expected = new List<(string Source, string Relative)>();
            if (rid.StartsWith("win-"))
            {
                AddNativeFiles(expected, Path.Combine(packages[rid], "runtimes", rid, "native"));
                var locales = Path.Combine(PackageDirectory(assets.RootElement, "chromiumembeddedframework.runtime"), "CEF", rid, "locales");
                AddNativeFiles(expected, locales, "locales/");
            }
            else
            {
                AddNativeFiles(expected, Path.Combine(packages[rid], "CEF"));
            }
            foreach (var file in expected)
            {
                var destination = Path.Combine(directory, file.Relative);
                Assert.IsTrue(File.Exists(destination), $"Missing CEF resource: {file.Relative}");
                Assert.AreEqual(HashFile(file.Source), HashFile(destination), $"CEF resource differs from {rid} package: {file.Relative}");
            }
            var runtimes = Path.Combine(directory, "runtimes");
            if (Directory.Exists(runtimes))
            {
                var cefNames = new[] { "win-x64", "win-arm64" }
                    .SelectMany(runtime => Directory.GetFiles(Path.Combine(packages[runtime], "runtimes", runtime, "native"), "*", SearchOption.AllDirectories))
                    .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(runtimes, "*", SearchOption.AllDirectories))
                {
                    Assert.IsFalse(cefNames.Contains(Path.GetFileName(file)), $"Duplicate CEF runtime asset: {file}");
                }
            }
            var sentinelName = $"cefglue-test-native-{rid}.bin";
            var sentinelCopies = Directory.GetFiles(directory, sentinelName, SearchOption.AllDirectories);
            Assert.AreEqual(1, sentinelCopies.Length, $"Unrelated native asset was removed or duplicated: {sentinelName}");
            Assert.AreEqual($"sentinel:{rid}", File.ReadAllText(sentinelCopies[0]));
            TestContext.Progress.WriteLine($"CEF_RESOURCES {rid} Files={expected.Count} HashesMatch=True NoDuplicateRuntimes=True UnrelatedNativePreserved=True");
        }

        private static string PackageDirectory(JsonElement assets, string id)
        {
            var matches = assets.GetProperty("libraries").EnumerateObject().Where(library => library.Name.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(1, matches.Length, $"Expected one resolved package: {id}");
            var relative = matches[0].Value.GetProperty("path").GetString()!;
            var paths = assets.GetProperty("packageFolders").EnumerateObject().Select(folder => Path.Combine(folder.Name, relative));
            var directory = paths.FirstOrDefault(Directory.Exists);
            Assert.IsNotNull(directory, $"Resolved package is missing from the NuGet cache: {id}");
            return directory!;
        }

        private static void AddNativeFiles(List<(string Source, string Relative)> expected, string sourceDirectory, string prefix = "")
        {
            Assert.IsTrue(Directory.Exists(sourceDirectory), $"Missing native package resources: {sourceDirectory}");
            var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
            Assert.IsNotEmpty(files, $"Empty native package resources: {sourceDirectory}");
            foreach (var file in files) { expected.Add((file, prefix + Path.GetRelativePath(sourceDirectory, file))); }
        }

        private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name)
        {
            var entry = archive.GetEntry(name);
            Assert.IsNotNull(entry, $"Missing package payload: {name}");
            Assert.Greater(entry!.Length, 0, $"Empty package payload: {name}");
            return entry;
        }

        private static bool IsAnyCpu(PEReader pe)
        {
            var flags = pe.PEHeaders.CorHeader?.Flags;
            return flags != null && pe.PEHeaders.CoffHeader.Machine == Machine.I386 && ((int)flags & 1) == 1 && ((int)flags & 0x20002) == 0;
        }

        private static void RuntimeIsFrameworkDependent(Stream stream)
        {
            using var document = JsonDocument.Parse(stream);
            var options = document.RootElement.GetProperty("runtimeOptions");
            Assert.AreEqual("net8.0", options.GetProperty("tfm").GetString());
            Assert.AreEqual("Major", options.GetProperty("rollForward").GetString());
            Assert.IsFalse(options.TryGetProperty("includedFrameworks", out _), "BrowserProcess must not carry a private runtime.");
            Assert.AreEqual("Microsoft.NETCore.App", options.GetProperty("framework").GetProperty("name").GetString());
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Hash(stream);
        }

        private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    }
}
