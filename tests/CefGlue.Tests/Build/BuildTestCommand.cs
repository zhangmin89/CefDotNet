using System.Diagnostics;
using System.Text.Json;
using NUnit.Framework;

namespace CefGlue.Tests.Build
{
    internal sealed class BuildTestCommand
    {
        private readonly string _logDirectory;
        private int _commandNumber;

        public BuildTestCommand(string logDirectory)
        {
            _logDirectory = logDirectory;
            Directory.CreateDirectory(logDirectory);
        }

        public async Task<string> RunAsync(string name, string fileName, string workingDirectory, IEnumerable<string> arguments, bool startup = false)
        {
            var commandName = $"{++_commandNumber:D2}-{name}";
            var stdoutPath = Path.Combine(_logDirectory, commandName + ".stdout.log");
            var stderrPath = Path.Combine(_logDirectory, commandName + ".stderr.log");
            var resultPath = Path.Combine(_logDirectory, commandName + ".command.json");
            var info = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
            // The diagnostics summarizer parses MSBuild's performance section headings.
            if (info.ArgumentList.Contains("-clp:PerformanceSummary")) { info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en"; }
            info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            info.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            TestContext.Progress.WriteLine($"COMMAND {fileName} {string.Join(' ', info.ArgumentList)}\nWORKING_DIRECTORY {workingDirectory}");

            await using var stdout = new FileStream(stdoutPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
            await using var stderr = new FileStream(stderrPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
            using var process = new Process { StartInfo = info };
            var stopwatch = Stopwatch.StartNew();
            Assert.IsTrue(process.Start(), $"Could not start {fileName}");
            var stdoutCopy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            var stderrCopy = process.StandardError.BaseStream.CopyToAsync(stderr);
            var previous = Snapshot(process, stdoutPath, stderrPath);
            var idleWaits = 0;
            try
            {
                while (!process.HasExited)
                {
                    process.WaitForExit(startup ? 10000 : 60000);
                    var current = Snapshot(process, stdoutPath, stderrPath);
                    var progress = current != previous;
                    TestContext.Progress.WriteLine($"PROGRESS {progress}");
                    idleWaits = progress ? 0 : idleWaits + 1;
                    if (idleWaits == 2) { throw new TimeoutException($"No progress after two waits: PID {process.Id}; logs: {stdoutPath}, {stderrPath}"); }
                    previous = current;
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    // Only terminate the process tree created by this command, so the next test cannot inherit it.
                    process.Kill(entireProcessTree: true);
                    var beforeExit = Snapshot(process, stdoutPath, stderrPath);
                    var exited = process.WaitForExit(10000);
                    var afterExit = Snapshot(process, stdoutPath, stderrPath);
                    TestContext.Progress.WriteLine($"PROGRESS {beforeExit != afterExit}");
                    if (!exited) { throw new TimeoutException($"Test command PID {process.Id} did not exit after termination."); }
                }
                await Task.WhenAll(stdoutCopy, stderrCopy);
                await stdout.FlushAsync();
                await stderr.FlushAsync();
                await stdout.DisposeAsync();
                await stderr.DisposeAsync();
                var result = new { Name = name, PID = process.Id, WorkingDirectory = workingDirectory, FilePath = fileName, Arguments = info.ArgumentList.ToArray(), process.ExitCode, Stdout = stdoutPath, Stderr = stderrPath, DurationSeconds = stopwatch.Elapsed.TotalSeconds };
                await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.AddTestAttachment(stdoutPath);
                TestContext.AddTestAttachment(stderrPath);
                TestContext.AddTestAttachment(resultPath);
                TestContext.Progress.WriteLine($"EXIT_CODE {process.ExitCode}");
            }
            Assert.AreEqual(0, process.ExitCode, $"Command '{name}' failed.\n{string.Join('\n', File.ReadLines(stdoutPath).TakeLast(12))}\n{string.Join('\n', File.ReadLines(stderrPath).TakeLast(12))}\nEvidence: {resultPath}");
            return stdoutPath;
        }

        private static ProgressSnapshot Snapshot(Process process, string stdoutPath, string stderrPath)
        {
            process.Refresh();
            var output = new FileInfo(stdoutPath);
            var error = new FileInfo(stderrPath);
            var exited = process.HasExited;
            var snapshot = new ProgressSnapshot(process.Id, !exited, exited ? process.ExitCode : null, exited ? null : process.TotalProcessorTime.TotalMilliseconds, output.Length, error.Length, output.LastWriteTimeUtc.Ticks, error.LastWriteTimeUtc.Ticks);
            TestContext.Progress.WriteLine(JsonSerializer.Serialize(snapshot));
            return snapshot;
        }

        private sealed record ProgressSnapshot(int PID, bool Alive, int? ExitCode, double? CpuMilliseconds, long StdoutBytes, long StderrBytes, long StdoutModified, long StderrModified);
    }
}
