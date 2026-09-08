using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

[assembly: CefGlue.Tests.Infrastructure.TestProgress]

namespace CefGlue.Tests.Infrastructure
{
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class TestProgressAttribute : TestActionAttribute
    {
        private static readonly string? directory = Environment.GetEnvironmentVariable("CEFGLUE_TEST_DIAGNOSTICS_DIR");
        private static readonly object sync = new object();
        private static readonly Dictionary<string, long> started = new Dictionary<string, long>();
        private static readonly TextWriter output = string.IsNullOrEmpty(directory)
            ? TextWriter.Null
            : new StreamWriter(Path.Combine(directory, $"test-events-{Environment.ProcessId}.jsonl"), append: true) { AutoFlush = true };

        public override ActionTargets Targets => ActionTargets.Test | ActionTargets.Suite;

        public override void BeforeTest(ITest test) => Record(test, "start");

        public override void AfterTest(ITest test) => Record(test, "end");

        private static void Record(ITest test, string eventName)
        {
            if (string.IsNullOrEmpty(directory)) { return; }
            lock (sync)
            {
                var timestamp = Stopwatch.GetTimestamp();
                var id = test.Id.ToString();
                double elapsedMs;
                if (eventName == "start")
                {
                    started.Add(id, timestamp);
                    elapsedMs = 0;
                }
                else
                {
                    elapsedMs = Stopwatch.GetElapsedTime(started[id], timestamp).TotalMilliseconds;
                    started.Remove(id);
                }
                output.WriteLine(JsonSerializer.Serialize(new { Event = eventName, Id = id, Name = test.FullName, IsSuite = test.IsSuite, Pid = Environment.ProcessId, Timestamp = timestamp, Frequency = Stopwatch.Frequency, Utc = DateTime.UtcNow, ElapsedMs = elapsedMs }));
            }
        }
    }
}
