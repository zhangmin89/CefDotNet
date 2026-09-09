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
        var marker = "review-trace-" + Guid.NewGuid().ToString("N");
        var logPath = Path.Combine(Path.GetTempPath(), marker + ".log");
        var context = new AssemblyLoadContext(marker, isCollectible: true);
        FileStream? lockedFile = null;
        TextWriter? writer = null;
        try
        {
            File.WriteAllText(logPath, "existing trace" + Environment.NewLine);
            var originalLength = new FileInfo(logPath).Length;
            if (locked) lockedFile = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Environment.SetEnvironmentVariable(switchName, enabled ? logPath : null);
            var assembly = context.LoadFromAssemblyPath(typeof(Deserializer).Assembly.Location);
            var traceType = assembly.GetType("Xilium.CefGlue.Common.Shared.Helpers.JavascriptExecutionTrace", throwOnError: true)!;
            Assert.AreEqual(enabled, traceType.GetProperty("IsEnabled")!.GetValue(null));
            writer = (TextWriter)traceType.GetField("output", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            traceType.GetMethod("Write")!.Invoke(null, new object[] { 42, 7L, marker, "test" });
            writer.Dispose();
            writer = null;
            if (enabled && !locked)
            {
                var text = File.ReadAllText(logPath);
                StringAssert.StartsWith("existing trace" + Environment.NewLine, text);
                StringAssert.Contains($"task=42 frame=7 stage={marker} test", text);
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
            File.Delete(logPath);
        }
    }

    [TestCase(null, false)]
    [TestCase("0", false)]
    [TestCase("relative.log", false)]
    [TestCase("1", true)]
    public void TraceSwitchPreservesDefaultAndDisabledValues(string? value, bool enabled)
    {
        const string switchName = "CEFGLUE_TRACE_JAVASCRIPT";
        var previousSwitch = Environment.GetEnvironmentVariable(switchName);
        var context = new AssemblyLoadContext("review-trace-switch", isCollectible: true);
        TextWriter? writer = null;
        try
        {
            Environment.SetEnvironmentVariable(switchName, value);
            var assembly = context.LoadFromAssemblyPath(typeof(Deserializer).Assembly.Location);
            var traceType = assembly.GetType("Xilium.CefGlue.Common.Shared.Helpers.JavascriptExecutionTrace", throwOnError: true)!;
            writer = (TextWriter)traceType.GetField("output", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.AreEqual(enabled, traceType.GetProperty("IsEnabled")!.GetValue(null));
            if (!enabled) Assert.AreSame(TextWriter.Null, writer);
        }
        finally
        {
            writer?.Dispose();
            Environment.SetEnvironmentVariable(switchName, previousSwitch);
            context.Unload();
        }
    }
}
