using System.Diagnostics;
using NUnit.Framework;

namespace CefGlue.Tests.Helpers
{
    internal static class TestDiagnostics
    {
        private static readonly string? directory = Environment.GetEnvironmentVariable("CEFGLUE_TEST_DIAGNOSTICS_DIR");
        private static readonly TextWriter output = string.IsNullOrEmpty(directory)
            ? TextWriter.Null
            : TextWriter.Synchronized(new StreamWriter(Path.Combine(directory, $"test-phases-{Environment.ProcessId}.log"), append: true) { AutoFlush = true });

        public static void Write(string testName, string stage, string details = "")
        {
            if (!string.IsNullOrEmpty(directory))
            {
                var line = $"[test-phase] utc={DateTime.UtcNow:O} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency} pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId} test={testName} stage={stage} {details}";
                output.WriteLine(line);
                TestContext.Progress.WriteLine(line);
            }
        }
    }
}
