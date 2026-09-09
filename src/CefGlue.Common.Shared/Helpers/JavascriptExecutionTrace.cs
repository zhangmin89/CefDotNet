using System;
using System.Diagnostics;
using System.IO;

namespace Xilium.CefGlue.Common.Shared.Helpers
{
    internal static class JavascriptExecutionTrace
    {
        private static readonly string? setting = Environment.GetEnvironmentVariable("CEFGLUE_TRACE_JAVASCRIPT");
        public static bool IsEnabled { get; } = setting == "1" || (setting != null && Path.IsPathFullyQualified(setting));
        private static readonly TextWriter output = CreateOutput();

        private static TextWriter CreateOutput()
        {
            if (!IsEnabled)
            {
                return TextWriter.Null;
            }

            try
            {
                var path = setting == "1" ? Path.Combine(AppContext.BaseDirectory, $"javascript-execution-{Environment.ProcessId}.log") : setting!;
                return TextWriter.Synchronized(new StreamWriter(path, append: true) { AutoFlush = true });
            }
            catch (IOException)
            {
                return TextWriter.Null;
            }
            catch (UnauthorizedAccessException)
            {
                return TextWriter.Null;
            }
        }

        public static void Write(int taskId, long frameIdentifier, string stage, string details)
        {
            if (IsEnabled)
            {
                output.WriteLine(FormattableString.Invariant($"[cef-js] utc={DateTime.UtcNow:O} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency} pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId} task={taskId} frame={frameIdentifier} stage={stage} {details}"));
            }
        }
    }
}
