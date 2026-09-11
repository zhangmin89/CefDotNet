using System;
using System.Reflection;
using System.Runtime.InteropServices;
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
        using var host = nativeBrowser.GetHost();
        var debugger = new DebuggerObserver();
        using var registration = host.AddDevToolsMessageObserver(debugger);
        using var parameters = CefDictionaryValue.Create();
        Task<int>? pending = null;
        try
        {
            await OnCefUi(() => Assert.AreEqual(DebuggerObserver.EnableId, host.ExecuteDevToolsMethod(DebuggerObserver.EnableId, "Debugger.enable", parameters)));
            await debugger.Enabled.Task.WaitAsync(Deadline);
            await OnCefUi(() =>
            {
                pending = Browser.EvaluateJavaScript<int>("debugger; return 7;", frame, timeout: null);
            });
            // The pause event proves this evaluation is executing and cannot return 7 before the crash.
            // Loading chrome://crash and sending an evaluation are asynchronous operations with no ordering guarantee.
            await debugger.Paused.Task.WaitAsync(Deadline);
            Assert.IsFalse(pending!.IsCompleted, "The evaluation must still be pending while paused in the renderer.");
            Assert.IsFalse(observer.Terminated.Task.IsCompleted);
            TestContext.Out.WriteLine($"Renderer paused; pending status before crash={pending.Status}");
            await OnCefUi(() => Assert.AreEqual(DebuggerObserver.CrashId, host.ExecuteDevToolsMethod(DebuggerObserver.CrashId, "Page.crash", parameters)));
            var status = await observer.Terminated.Task.WaitAsync(Deadline);
            TestContext.Out.WriteLine($"Renderer termination={status}; pending status after notification={pending.Status}");
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

    private static Task OnCefUi(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(CefRuntime.PostTask(CefThreadId.UI, new CallbackTask(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        })));
        return completion.Task.WaitAsync(Deadline);
    }

    private sealed class DebuggerObserver : CefDevToolsMessageObserver
    {
        public const int EnableId = 1;
        public const int CrashId = 2;
        public readonly TaskCompletionSource Enabled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override bool OnDevToolsMessage(CefBrowser browser, IntPtr message, int messageSize) => false;
        protected override void OnDevToolsMethodResult(CefBrowser browser, int messageId, bool success, IntPtr result, int resultSize)
        {
            if (messageId == EnableId)
            {
                if (success) { Enabled.TrySetResult(); }
                else { Enabled.TrySetException(new InvalidOperationException($"Debugger.enable failed: {Marshal.PtrToStringUTF8(result, resultSize)}")); }
            }
            else if (messageId == CrashId)
            {
                // Crashing can fail the pending DevTools command with "Target crashed"; verify the CEF termination callback instead.
                Console.WriteLine($"Page.crash response: success={success}; {Marshal.PtrToStringUTF8(result, resultSize)}");
            }
        }
        protected override void OnDevToolsEvent(CefBrowser browser, string method, IntPtr parameters, int parametersSize)
        {
            if (method == "Debugger.paused") { Paused.TrySetResult(); }
        }
        protected override void OnDevToolsAgentAttached(CefBrowser browser) { }
        protected override void OnDevToolsAgentDetached(CefBrowser browser) { }
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
