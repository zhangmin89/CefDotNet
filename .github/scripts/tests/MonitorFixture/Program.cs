using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length != 2 || args[0] is not ("success" or "failure" or "sequence" or "hang" or "child"))
{
    throw new ArgumentException("Expected a fixture mode and output directory.");
}

var mode = args[0];
var outputDirectory = Path.GetFullPath(args[1]);
if (!Directory.Exists(outputDirectory))
{
    throw new DirectoryNotFoundException(outputDirectory);
}

Console.WriteLine($"fixture stdout: {mode}");
Console.Error.WriteLine($"fixture stderr: {mode}");
Console.WriteLine($"fixture runtime: {RuntimeInformation.FrameworkDescription}; host: {Environment.ProcessPath}");
if (mode == "success") return 0;
if (mode == "failure") return 7;

void WriteTestEvent(string id, string eventName)
{
    var directory = Environment.GetEnvironmentVariable("CEFGLUE_TEST_DIAGNOSTICS_DIR") ?? throw new InvalidOperationException("Missing monitor diagnostics directory.");
    var path = Path.Combine(directory, $"test-events-{Environment.ProcessId}.jsonl");
    var value = new { Event = eventName, Id = id, Name = $"Fixture.{id}", IsSuite = false, Pid = Environment.ProcessId, Timestamp = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency, ElapsedMs = 0 };
    File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine);
}

if (mode == "sequence")
{
    foreach (var id in new[] { "first", "second" })
    {
        WriteTestEvent(id, "start");
        Thread.Sleep(10000);
        WriteTestEvent(id, "end");
    }
    return 0;
}

if (mode == "hang")
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "child", outputDirectory })
    {
        info.ArgumentList.Add(argument);
    }
    using var child = Process.Start(info) ?? throw new InvalidOperationException("Could not start fixture child.");
    File.WriteAllText(Path.Combine(outputDirectory, "child.json"), JsonSerializer.Serialize(new { child.Id, StartTicks = child.StartTime.ToUniversalTime().Ticks }));
    WriteTestEvent("hang", "start");
    while (true)
    {
        Console.WriteLine("Fixture.hang still produces output, but has not completed.");
        Thread.Sleep(10000);
    }
}

Thread.Sleep(Timeout.Infinite);
return 0;
