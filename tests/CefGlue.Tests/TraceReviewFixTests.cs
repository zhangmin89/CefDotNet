using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using NUnit.Framework;
using Xilium.CefGlue.Common.Shared.Serialization;

namespace CefGlue.Tests;

[TestFixture, NonParallelizable]
public class TraceReviewFixTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void OptionalTraceDoesNotFailWhenItsFileCannotBeOpened(bool enabled, bool locked)
    {
        const string switchName = "CEFGLUE_TRACE_JAVASCRIPT";
        var previousSwitch = Environment.GetEnvironmentVariable(switchName);
        var logPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"javascript-execution-{Environment.ProcessId}.log"));
        var existed = File.Exists(logPath);
        var originalLength = existed ? new FileInfo(logPath).Length : 0;
        var marker = "review-trace-" + Guid.NewGuid().ToString("N");
        var context = new AssemblyLoadContext(marker, isCollectible: true);
        FileStream? lockedFile = null;
        TextWriter? writer = null;
        try
        {
            if (locked) lockedFile = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Environment.SetEnvironmentVariable(switchName, enabled ? "1" : null);
            var assembly = context.LoadFromAssemblyPath(typeof(Deserializer).Assembly.Location);
            var traceType = assembly.GetType("Xilium.CefGlue.Common.Shared.Helpers.JavascriptExecutionTrace", throwOnError: true)!;
            Assert.AreEqual(enabled, traceType.GetProperty("IsEnabled")!.GetValue(null));
            writer = (TextWriter)traceType.GetField("output", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            traceType.GetMethod("Write")!.Invoke(null, new object[] { 42, 7L, marker, "test" });
            writer.Dispose();
            writer = null;
            if (enabled && !locked)
            {
                StringAssert.Contains($"task=42 frame=7 stage={marker} test", File.ReadAllText(logPath));
                Assert.Greater(new FileInfo(logPath).Length, originalLength);
            }
            else
            {
                var length = lockedFile?.Length ?? (File.Exists(logPath) ? new FileInfo(logPath).Length : 0);
                Assert.AreEqual(originalLength, length);
            }
        }
        finally
        {
            writer?.Dispose();
            lockedFile?.Dispose();
            Environment.SetEnvironmentVariable(switchName, previousSwitch);
            context.Unload();
            if (!existed && File.Exists(logPath)) File.Delete(logPath);
        }
    }
}
