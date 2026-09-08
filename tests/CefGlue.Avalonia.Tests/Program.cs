using Avalonia.Threading;
using NUnit.Framework.Api;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace CefGlue.Tests
{
    // AppKit needs the process main thread; VSTest invokes fixtures on worker threads.
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: CefGlue.Avalonia.Tests <NUnit result XML path>");
                return 1;
            }

            try
            {
                var resultPath = Path.GetFullPath(args[0]);
                // Match the test output directory used by VSTest for relative CEF cache paths.
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);
                TestBase.InitializeApplication();
                // The CI process monitor collects diagnostics before enforcing the timeout.
                using var lifetime = new CancellationTokenSource();
                var tests = Task.Run(() =>
                {
                    var runner = new NUnitTestAssemblyRunner(new DefaultTestAssemblyBuilder());
                    runner.Load(typeof(Program).Assembly, new Dictionary<string, object>());
                    var result = runner.Run(new ConsoleTestListener(), TestFilter.Empty);
                    Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
                    File.WriteAllText(resultPath, result.ToXml(true).OuterXml);
                    Console.WriteLine($"Passed: {result.PassCount}, Failed: {result.FailCount}, Skipped: {result.SkipCount}; results: {resultPath}");
                    return result.ResultState.Status == TestStatus.Passed && result.PassCount > 0 ? 0 : 1;
                });
                _ = tests.ContinueWith(_ => Dispatcher.UIThread.Post(lifetime.Cancel), TaskScheduler.Default);

                Dispatcher.UIThread.MainLoop(lifetime.Token);
                return tests.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private sealed class ConsoleTestListener : ITestListener
        {
            // NUnit replaces Console writers while tests run. Keep the original sinks.
            private readonly TextWriter output = Console.Out;
            private readonly TextWriter error = Console.Error;

            public void TestStarted(ITest test)
            {
                if (!test.IsSuite)
                {
                    output.WriteLine($"Running: {test.FullName}");
                }
            }
            public void TestFinished(ITestResult result)
            {
                if (!result.Test.IsSuite)
                {
                    output.WriteLine($"{result.ResultState}: {result.FullName}");
                    if (result.ResultState.Status == TestStatus.Failed)
                    {
                        error.WriteLine(result.Message);
                        error.WriteLine(result.StackTrace);
                    }
                }
            }
            public void TestOutput(TestOutput testOutput) => output.Write(testOutput.Text);
            public void SendMessage(TestMessage message) => output.WriteLine(message.Message);
        }
    }
}
