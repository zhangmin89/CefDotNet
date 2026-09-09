using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Handlers;
using Xilium.CefGlue.Common.JavascriptExecution;

namespace CefGlue.Tests;

[TestFixture, NonParallelizable, Category("RendererTerminationReviewFix")]
[Explicit("Runs a controlled renderer crash in a separate test process.")]
[CancelAfter(60000)]
public class RendererTerminationReviewFixTests : TestBase
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Test]
    public async Task RendererTerminationCompletesPendingEvaluationAndAllowsReload()
    {
        var observer = new CrashObserver();
        Browser.RequestHandler = observer;
        await Browser.LoadContent("<html><body>renderer termination review</body></html>").WaitAsync(Deadline);
        Assert.AreEqual(42, await EvaluateJavascript<int>("return 6 * 7;", Deadline));
        var adapter = (CommonBrowserAdapter)typeof(BaseCefBrowser).GetField("_adapter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Browser)!;
        var engine = (JavascriptExecutionEngine)typeof(CommonBrowserAdapter).GetField("_javascriptExecutionEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var nativeBrowser = (CefBrowser)typeof(CommonBrowserAdapter).GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        using var frame = nativeBrowser.GetMainFrame();
        Task<int>? pending = null;
        try
        {
            var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(CefRuntime.PostTask(CefThreadId.UI, new CallbackTask(() =>
            {
                try
                {
                    frame.LoadUrl("chrome://crash");
                    pending = Browser.EvaluateJavaScript<int>("return 7;", frame, timeout: null);
                    Assert.IsFalse(pending.IsCompleted);
                    Assert.IsFalse(observer.Terminated.Task.IsCompleted, "Register the evaluation before the UI-thread termination callback.");
                    submitted.TrySetResult();
                }
                catch (Exception exception) { submitted.TrySetException(exception); }
            })));
            await submitted.Task.WaitAsync(Deadline);
            var status = await observer.Terminated.Task.WaitAsync(Deadline);
            TestContext.Out.WriteLine($"Renderer termination={status}; pending status at callback={pending!.Status}");
            Assert.AreEqual(0, await pending!.WaitAsync(Deadline), "Renderer termination keeps the existing cancellation result contract.");
            await Browser.LoadContent("<html><body>renderer recovered</body></html>").WaitAsync(Deadline);
            Assert.AreEqual(42, await EvaluateJavascript<int>("return 6 * 7;", Deadline));
            Assert.AreEqual(1, observer.Calls);
        }
        finally
        {
            if (pending != null && !pending.IsCompleted)
            {
                engine.Dispose();
                await pending.WaitAsync(Deadline);
            }
        }
    }

    private sealed class CallbackTask : CefTask
    {
        private readonly Action _action;
        public CallbackTask(Action action) => _action = action;
        protected override void Execute() => _action();
    }

    private sealed class CrashObserver : RequestHandler
    {
        public readonly TaskCompletionSource<CefTerminationStatus> Terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override void OnRenderProcessTerminated(CefBrowser browser, CefTerminationStatus status)
        {
            Calls++;
            Terminated.TrySetResult(status);
            // Deliberately omit base: user overrides must not disable internal pending-task cleanup.
        }
    }
}
