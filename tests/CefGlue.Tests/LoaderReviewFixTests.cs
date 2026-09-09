using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Xilium.CefGlue.Common;

namespace CefGlue.Tests;

[TestFixture]
public class LoaderReviewFixTests
{
    private static string HelperName => "Xilium.CefGlue.BrowserProcess" + (OperatingSystem.IsWindows() ? ".exe" : "");

    [Test]
    public void AssemblyWithoutLocationOnlyProbesTheApplicationDirectory()
    {
        var assembly = Assembly.Load(File.ReadAllBytes(typeof(CefRuntimeLoader).Assembly.Location));
        Assert.AreEqual(string.Empty, assembly.Location);
        var directory = Path.Combine(AppContext.BaseDirectory, "loader-review-" + Guid.NewGuid().ToString("N"));
        CollectionAssert.AreEqual(new[] { Path.Combine(directory, HelperName) }, GetPaths(assembly, directory).ToArray());
    }

    [Test]
    public void ExistingApplicationHelperIsSelectedWithoutAnAssemblyLocation()
    {
        var assembly = Assembly.Load(File.ReadAllBytes(typeof(CefRuntimeLoader).Assembly.Location));
        Assert.AreEqual(string.Empty, assembly.Location);
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "loader-review-" + Guid.NewGuid().ToString("N")));
        var helper = Path.Combine(directory, HelperName);
        Assert.IsFalse(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        try
        {
            using (var file = new FileStream(helper, FileMode.CreateNew, FileAccess.Write)) file.WriteByte(42);
            Assert.AreEqual(helper, GetPaths(assembly, directory).FirstOrDefault(File.Exists));
        }
        finally
        {
            File.Delete(helper);
            Directory.Delete(directory);
        }
    }

    [Test]
    public void LocatedAssemblyRetainsThePluginDirectoryAndProbeOrder()
    {
        var assembly = typeof(CefRuntimeLoader).Assembly;
        var directory = Path.Combine(AppContext.BaseDirectory, "loader-review-" + Guid.NewGuid().ToString("N"));
        CollectionAssert.AreEqual(new[] { Path.Combine(directory, HelperName), Path.Combine(Path.GetDirectoryName(assembly.Location)!, HelperName) }, GetPaths(assembly, directory).ToArray());
    }

    private static IEnumerable<string> GetPaths(Assembly assembly, string directory)
    {
        var loader = assembly.GetType(typeof(CefRuntimeLoader).FullName!, throwOnError: true)!;
        return (IEnumerable<string>)loader.GetMethod("GetSubProcessPaths", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { directory })!;
    }
}
