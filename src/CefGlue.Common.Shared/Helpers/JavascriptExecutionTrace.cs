using System;
using System.Diagnostics;
using System.IO;

namespace Xilium.CefGlue.Common.Shared.Helpers
{
    internal static class JavascriptExecutionTrace
    {
        public static bool IsEnabled { get; } = Environment.GetEnvironmentVariable("CEFGLUE_TRACE_JAVASCRIPT") == "1";
        private static readonly TextWriter output = CreateOutput();

        private static TextWriter CreateOutput()
        {
            if (!IsEnabled)
            {
                return TextWriter.Null;
            }

            try
            {
                return TextWriter.Synchronized(new StreamWriter(Path.Combine(AppContext.BaseDirectory, $"javascript-execution-{Environment.ProcessId}.log"), append: true) { AutoFlush = true });
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
