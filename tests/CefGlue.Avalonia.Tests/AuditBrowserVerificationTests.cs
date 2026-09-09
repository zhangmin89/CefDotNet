using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Avalonia.Platform;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Handlers;
using Xilium.CefGlue.Common.InternalHandlers;
using Xilium.CefGlue.Common.JavascriptExecution;

namespace CefGlue.Tests;

// Live CEF/OSR probes. Observational tests log results rather than assuming every allegation is true.
[TestFixture, NonParallelizable, Category("AuditBrowserVerification")]
[Explicit("Manual CEF audit session; run separately from the ordinary browser test fixtures.")]
[CancelAfter(60000)]
public class AuditBrowserVerificationTests
{
    private AvaloniaCefBrowser browser = null!;
    private Window window = null!;
    private Lifecycle lifecycle = null!;
    private CrashObserver crashObserver = null!;
    private CefBrowser nativeBrowser = null!;
    private CefBrowserHost nativeHost = null!;
    private readonly ConcurrentQueue<string> errors = new();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private CommonBrowserAdapter Adapter => (CommonBrowserAdapter)GetField(browser, "_adapter")!;
    private JavascriptExecutionEngine Engine => (JavascriptExecutionEngine)GetField(Adapter, "_javascriptExecutionEngine")!;

    private static object? GetField(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target);
        }
        throw new MissingFieldException(target.GetType().FullName, name);
    }

    private static Task UI(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
    private static Task<T> UI<T>(Func<T> action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
    private Task<T> JS<T>(string script) => browser.EvaluateJavaScript<T>(script, timeout: Deadline);

    [OneTimeSetUp]
    public async Task Initialize()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                CefRuntimeLoader.Initialize(new CefSettings { WindowlessRenderingEnabled = true, RootCachePath = Path.Combine(Path.GetTempPath(), "cef-audit-" + Guid.NewGuid().ToString("N")), LogFile = Path.Combine(AppContext.BaseDirectory, "cef-audit.log") }, new[] { KeyValuePair.Create("disable-popup-blocking", "") });
                AppBuilder.Configure<Helpers.App>().UsePlatformDetect().SetupWithoutStarting();
                Dispatcher.UIThread.Post(() => ready.SetResult());
                Dispatcher.UIThread.MainLoop(CancellationToken.None);
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true, Name = "CEF audit UI" };
        thread.Start();
        await ready.Task.WaitAsync(Deadline);
    }

    [SetUp]
    public async Task OpenBrowser()
    {
        while (errors.TryDequeue(out _)) { }
        lifecycle = new Lifecycle();
        crashObserver = new CrashObserver();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await UI(() =>
        {
            window = new Window { Width = 64, Height = 64, ShowInTaskbar = false, Title = "CefGlue audit verification" };
            browser = new AvaloniaCefBrowser { Width = 64, Height = 64, LifeSpanHandler = lifecycle, RequestHandler = crashObserver };
            browser.BrowserInitialized += () => initialized.TrySetResult();
            browser.UnhandledException += (_, args) => errors.Enqueue(args.Exception.ToString());
            window.Content = browser;
            window.Show();
        });
        await initialized.Task.WaitAsync(Deadline);
        nativeBrowser = (CefBrowser)GetField(Adapter, "_browser")!;
        nativeHost = nativeBrowser.GetHost();
        await browser.LoadContent("<html><body style='margin:0;background:blue'>audit</body></html>").WaitAsync(Deadline);
    }

    [TearDown]
    public async Task CloseBrowser()
    {
        try
        {
            if (lifecycle.Popup != null && !lifecycle.PopupClosed.Task.IsCompleted) lifecycle.Popup.GetHost().CloseBrowser(true);
            if (!lifecycle.MainClosed.Task.IsCompleted) nativeHost.CloseBrowser(true);
            await lifecycle.MainClosed.Task.WaitAsync(Deadline);
            browser.Dispose();
            await UI(() => window.Close());
        }
        finally
        {
            foreach (var error in errors) TestContext.Out.WriteLine("BROWSER_ERROR " + error);
        }
    }

    [Test]
    public async Task B00_LiveOsrSmoke()
    {
        Assert.IsTrue(nativeHost.IsWindowRenderingDisabled);
        Assert.AreEqual(4, await JS<int>("return 2 + 2;"));
        TestContext.Out.WriteLine($"Live CEF OSR=true; native browser={nativeBrowser.Identifier}; JavaScript=4");
    }

    [Test]
    public async Task V04_DisposedEngineCanStillReceiveRealReply()
    {
        Engine.Dispose();
        using var frame = nativeBrowser.GetMainFrame();
        var result = await Engine.Evaluate<int>("return 6 * 7;", "", 1, frame).WaitAsync(Deadline);
        TestContext.Out.WriteLine($"V04 engine disposed; frameValid={frame.IsValid}; real IPC result={result}");
        Assert.AreEqual(42, result);
    }

    [Test]
    public async Task V03_RendererCrashPendingEvaluation()
    {
        var pending = browser.EvaluateJavaScript<int>("let until=Date.now()+10000; while(Date.now()<until){}; return 7;");
        browser.Address = "chrome://crash";
        var status = await crashObserver.Crashed.Task.WaitAsync(Deadline);
        var completed = await Task.WhenAny(pending, Task.Delay(Deadline)) == pending;
        TestContext.Out.WriteLine($"V03 observed real renderer termination={status}; evaluationCompletedAfter10s={completed}; taskStatus={pending.Status}");
        Engine.Dispose();
        await pending.WaitAsync(Deadline);
        Assert.IsFalse(completed);
    }

    [Test]
    public async Task V05_NavigationEvaluationStress()
    {
        var tasks = new List<Task<int>>();
        for (var batch = 0; batch < 10; batch++)
        {
            for (var index = 0; index < 20; index++) tasks.Add(browser.EvaluateJavaScript<int>("return 1;"));
            await browser.LoadContent("<html><body>navigation " + batch + "</body></html>").WaitAsync(Deadline);
        }
        try { await Task.WhenAll(tasks).WaitAsync(Deadline); }
        catch (Exception exception) { TestContext.Out.WriteLine("V05 completion wait: " + exception); }
        TestContext.Out.WriteLine($"V05 evaluations={tasks.Count}; completed={tasks.Count(t => t.IsCompleted)}; faulted={tasks.Count(t => t.IsFaulted)}; pending={tasks.Count(t => !t.IsCompleted)}; engineErrors={errors.Count}");
        Engine.Dispose();
    }

    public class Target
    {
        public int Echo(int value) => value;
        public void Ping() { }
    }

    [Test]
    public async Task V06_InvalidNativeArgumentLeavesPromisePending()
    {
        browser.RegisterJavascriptObject(new Target(), "auditNative");
        Assert.IsTrue(await JS<bool>("return typeof auditNative === 'object';"));
        var received = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.UnhandledException += (_, args) => received.TrySetResult(args.Exception);
        await JS<bool>("window.auditState='pending'; auditNative.echo('invalid').then(()=>auditState='resolved',()=>auditState='rejected'); return true;");
        var error = await received.Task.WaitAsync(Deadline);
        var actual = await JS<string>("return auditState;");
        TestContext.Out.WriteLine($"V06 real JS promise={actual}; browser still responds={await JS<int>("return 2+2;")}; exception={error}");
        Assert.AreEqual("pending", actual);
    }

    [Test]
    public async Task V10_LateBindingContextReplacementStress()
    {
        for (var index = 0; index < 30; index++)
        {
            await JS<bool>("window.auditPending=cefglue.checkObjectBound('auditMissing'); return true;");
            browser.RegisterJavascriptObject(new Target(), "auditMissing");
            await browser.LoadContent("<html><body>replacement " + index + "</body></html>").WaitAsync(Deadline);
            browser.UnregisterJavascriptObject("auditMissing");
        }
        TestContext.Out.WriteLine($"V10 replacements=30; errors={errors.Count}; finalLiveResult={await JS<int>("return 42;")}");
    }

    [Test]
    public async Task V11_PromiseFactoryFailureIsReachable()
    {
        browser.RegisterJavascriptObject(new Target(), "auditNative");
        await JS<bool>("window.auditFactory=cefglue.createPromise; cefglue.createPromise=function(){throw Error('audit factory failure');}; return true;");
        Exception? failure = null;
        try { await JS<object>("return auditNative.ping();"); }
        catch (Exception exception) { failure = exception; }
        finally { await JS<bool>("cefglue.createPromise=window.auditFactory; return true;"); }
        TestContext.Out.WriteLine($"V11 overridden promise factory; observed evaluation error={failure}; browser alive={await JS<int>("return 42;")}; retained native refs not measured");
        Assert.IsNotNull(failure);
    }

    [Test]
    public async Task V15_RegistryCloseRace()
    {
        var failures = new ConcurrentQueue<string>();
        var registration = Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                try { browser.RegisterJavascriptObject(new Target(), "auditRace"); browser.UnregisterJavascriptObject("auditRace"); }
                catch (Exception exception) { failures.Enqueue(exception.ToString()); }
            }
        });
        nativeHost.CloseBrowser(true);
        await lifecycle.MainClosed.Task.WaitAsync(Deadline);
        await registration.WaitAsync(Deadline);
        TestContext.Out.WriteLine($"V15 1000 register/unregister iterations concurrent with actual close; exceptions={failures.Count}");
        foreach (var error in failures.Take(3)) TestContext.Out.WriteLine(error);
    }

    [Test]
    public async Task V16_CloseCallbackWrapperIdentity()
    {
        var identity = new TaskCompletionSource<(bool Same, bool Valid)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lifecycle.ClosingObserver = callbackBrowser => identity.TrySetResult((ReferenceEquals(nativeBrowser, callbackBrowser), nativeBrowser.IsValid));
        nativeHost.CloseBrowser(true);
        await lifecycle.MainClosed.Task.WaitAsync(Deadline);
        var observed = await identity.Task.WaitAsync(Deadline);
        TestContext.Out.WriteLine($"V16 ReferenceEquals(stored,closeCallback)={observed.Same}; storedValidDuringDoClose={observed.Valid}; storedValidAfterClose={nativeBrowser.IsValid}");
        Assert.IsFalse(observed.Same);
    }

    [Test]
    public async Task V19_V20_V24_ActualPopupLifecycle()
    {
        var console = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.ConsoleMessage += (_, args) => { if (args.Message == "audit-popup-console") console.TrySetResult(); };
        Assert.IsTrue(await JS<bool>("window.auditPopup=window.open('about:blank','audit','width=64,height=64'); return !!auditPopup;"));
        var popup = await lifecycle.PopupCreated.Task.WaitAsync(Deadline);
        TestContext.Out.WriteLine($"V20 actual window.open browser.IsPopup={popup.IsPopup}; popup OSR={popup.GetHost().IsWindowRenderingDisabled}; main OSR={nativeHost.IsWindowRenderingDisabled}");
        popup.GetMainFrame().ExecuteJavaScript("console.log('audit-popup-console');", "", 1);
        await console.Task.WaitAsync(Deadline);
        TestContext.Out.WriteLine("V24 popup console message arrived on main control event");
        using var capturedFrame = nativeBrowser.GetMainFrame();
        popup.GetHost().CloseBrowser(true);
        await lifecycle.PopupClosed.Task.WaitAsync(Deadline);
        TestContext.Out.WriteLine($"V19 after popup close: mainNativeValid={nativeBrowser.IsValid}; adapterBrowserNull={GetField(Adapter, "_browser") == null}; mainClosed={lifecycle.MainClosed.Task.IsCompleted}");
        Assert.IsNull(GetField(Adapter, "_browser"));
        Assert.IsTrue(nativeBrowser.IsValid);
        try
        {
            var value = await browser.EvaluateJavaScript<int>("return 42;", capturedFrame, timeout: Deadline).WaitAsync(Deadline);
            TestContext.Out.WriteLine($"V04 after real adapter Cleanup: capturedFrameValid={capturedFrame.IsValid}; public EvaluateJavaScript result={value}");
        }
        catch (Exception exception) { TestContext.Out.WriteLine("V04 after real Cleanup: " + exception); }
    }

    [Test]
    public async Task V20_ExplicitOsrPopupCanOverwriteMainBitmap()
    {
        lifecycle.ForcePopupOsr = true;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.ConsoleMessage += (_, args) => { if (args.Message == "audit-osr-popup-ready") ready.TrySetResult(); };
        Assert.IsTrue(await JS<bool>("window.auditPopup=window.open('about:blank','audit-osr','width=64,height=64'); return !!auditPopup;"));
        var popup = await lifecycle.PopupCreated.Task.WaitAsync(Deadline);
        var popupHost = popup.GetHost();
        Assert.IsTrue(popupHost.IsWindowRenderingDisabled);
        popup.GetMainFrame().ExecuteJavaScript("document.body.innerHTML=''; document.body.style.cssText='margin:0;background:rgb(255,0,0)'; requestAnimationFrame(()=>requestAnimationFrame(()=>console.log('audit-osr-popup-ready')));", "", 1);
        await ready.Task.WaitAsync(Deadline);
        popupHost.Invalidate(CefPaintElementType.View);
        await Task.Delay(Deadline);
        var pixel = await UI(() =>
        {
            var control = typeof(CommonBrowserAdapter).GetProperty("Control", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Adapter)!;
            var surface = ((AvaloniaOffScreenControlHost)control).RenderSurface;
            var bitmap = (WriteableBitmap)GetField(surface, "_bitmap")!;
            using var buffer = bitmap.Lock();
            return unchecked((uint)Marshal.ReadInt32(buffer.Address, buffer.RowBytes * (buffer.Size.Height / 2) + 4 * (buffer.Size.Width / 2)));
        });
        var mainColor = await JS<string>("return getComputedStyle(document.body).backgroundColor;");
        TestContext.Out.WriteLine($"V20 explicit windowless popup: popupOSR={popupHost.IsWindowRenderingDisabled}; main DOM background={mainColor}; main displayed bitmap center=0x{pixel:X8}; expected popup red=0xFFFF0000");
        Assert.AreEqual("rgb(0, 0, 255)", mainColor);
        Assert.AreEqual(0xFFFF0000u, pixel);
    }

    [Test]
    public async Task V21_CustomDoCloseTrue()
    {
        lifecycle.CustomClose = true;
        nativeHost.CloseBrowser(true);
        await lifecycle.MainClosed.Task.WaitAsync(Deadline);
        TestContext.Out.WriteLine($"V21 custom DoClose=true; actual OnBeforeClose received; adapterBrowserNull={GetField(Adapter, "_browser") == null}");
        Assert.IsNotNull(GetField(Adapter, "_browser"));
    }

    [Test]
    public void V22_DisplayCallbacksAndV23_ScreenPoint()
    {
        var observer = new DisplayObserver();
        browser.DisplayHandler = observer;
        var display = new CommonCefDisplayHandler(Adapter);
        typeof(CommonCefDisplayHandler).GetMethod("OnCursorChange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(display, new object?[] { nativeBrowser, IntPtr.Zero, CefCursorType.Pointer, null });
        typeof(CefDisplayHandler).GetMethod("OnMediaAccessChange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(display, new object[] { nativeBrowser, true, true });
        var client = GetField(Adapter, "_cefClient")!;
        var render = GetField(client, "_renderHandler")!;
        var args = new object[] { nativeBrowser, 15, 25, 0, 0 };
        var result = (bool)render.GetType().GetMethod("GetScreenPoint", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(render, args)!;
        TestContext.Out.WriteLine($"V22 user cursor calls={observer.CursorCalls}; media calls={observer.MediaCalls}; V23 success={result}; screen=({args[3]},{args[4]})");
        Assert.AreEqual(0, observer.CursorCalls);
        Assert.AreEqual(0, observer.MediaCalls);
        Assert.IsTrue(result);
        Assert.AreEqual(0, args[3]);
        Assert.AreEqual(0, args[4]);
    }

    [Test]
    public async Task V25_PopupDisposeKeepsWindowAlive()
    {
        await UI(() =>
        {
            var popup = new ExtendedAvaloniaPopup { PlacementTarget = browser, Width = 10, Height = 10, ShowInTaskbar = false };
            var closed = false;
            popup.Closed += (_, _) => closed = true;
            var host = new AvaloniaPopup(popup, popup.VisualChildren);
            popup.Show();
            host.Dispose();
            Dispatcher.UIThread.Post(() =>
            {
                TestContext.Out.WriteLine($"V25 after queued Dispose: visible={popup.IsVisible}; closedEvent={closed}; nativeHandle={popup.TryGetPlatformHandle()?.Handle}");
                popup.Show();
                TestContext.Out.WriteLine($"V25 can Show again={popup.IsVisible}");
                host.RenderSurface.Dispose();
                popup.Close();
            });
        });
        await UI(() => { });
    }

    [Test]
    public async Task V26_DisposedBitmapStillAssignedAndRenderAttempt()
    {
        Image image = null!;
        AvaloniaRenderSurface surface = null!;
        WriteableBitmap bitmap = null!;
        await UI(() =>
        {
            image = new Image();
            surface = new AvaloniaRenderSurface(image);
            typeof(AvaloniaRenderSurface).GetMethod("CreateBitmap", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(surface, new object[] { 8, 8 });
            bitmap = (WriteableBitmap)image.Source!;
            image.Measure(new Size(8, 8));
            image.Arrange(new Rect(0, 0, 8, 8));
        });
        await Task.Run(surface.Dispose);
        var evidence = await UI(() =>
        {
            Exception? error = null;
            try { using var target = new RenderTargetBitmap(new PixelSize(8, 8)); target.Render(image); }
            catch (Exception exception) { error = exception; }
            var evidence = $"V26 worker-thread Dispose; Image.Source retains disposed bitmap={ReferenceEquals(bitmap, image.Source)}; actual Render exception={error}";
            Assert.AreSame(bitmap, image.Source);
            image.Source = null;
            return evidence;
        });
        TestContext.Out.WriteLine(evidence);
    }

    [Test]
    public async Task V32_V33_InputAndV34_DragLeave()
    {
        var evidence = await UI(() =>
        {
            var control = new ExtendedAvaloniaPopup { ShowInTaskbar = false };
            var host = new AvaloniaOffScreenControlHost(control, control.VisualChildren);
            var pointer = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);
            var wheel = new PointerWheelEventArgs(control, pointer, control, new Point(), 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.Control | KeyModifiers.Shift, new Vector(0.25, -0.5));
            var x = int.MinValue;
            var y = int.MinValue;
            var flags = CefEventFlags.None;
            host.MouseWheelChanged += (mouse, dx, dy) => { x = dx; y = dy; flags = mouse.Modifiers; };
            typeof(AvaloniaOffScreenControlHost).GetMethod("OnPointerWheelChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, new object[] { control, wheel });
            var original = new Cursor(StandardCursorType.Arrow);
            var drag = new Cursor(StandardCursorType.Cross);
            typeof(AvaloniaOffScreenControlHost).GetField("_previousCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, original);
            control.Cursor = drag;
            typeof(AvaloniaOffScreenControlHost).GetMethod("OnDragLeave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, new object[] { control, new RoutedEventArgs() });
            var evidence = $"V32 input=(0.25,-0.5), output=({x},{y}); V33 Ctrl+Shift flags={flags}; V34 cursorRestored={ReferenceEquals(control.Cursor, original)}";
            Assert.AreEqual(0, x);
            Assert.AreEqual(0, y);
            Assert.AreEqual(CefEventFlags.None, flags);
            Assert.AreSame(drag, control.Cursor);
            host.RenderSurface.Dispose();
            control.Close();
            return evidence;
        });
        TestContext.Out.WriteLine(evidence);
    }

    private class Lifecycle : LifeSpanHandler
    {
        public readonly TaskCompletionSource<CefBrowser> PopupCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource MainClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource PopupClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CefBrowser? Popup;
        public bool ForcePopupOsr;
        public bool CustomClose;
        public Action<CefBrowser>? ClosingObserver;
        protected override void OnAfterCreated(CefBrowser browser) { if (browser.IsPopup) { Popup = browser; PopupCreated.TrySetResult(browser); } }
        protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, string targetUrl, string targetFrameName, CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
        {
            if (ForcePopupOsr) windowInfo.SetAsWindowless(IntPtr.Zero, false);
            return false;
        }
        protected override bool DoClose(CefBrowser browser) { ClosingObserver?.Invoke(browser); return CustomClose; }
        protected override void OnBeforeClose(CefBrowser browser) { if (browser.IsPopup) PopupClosed.TrySetResult(); else MainClosed.TrySetResult(); }
    }

    private class CrashObserver : RequestHandler
    {
        public readonly TaskCompletionSource<CefTerminationStatus> Crashed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void OnRenderProcessTerminated(CefBrowser browser, CefTerminationStatus status) => Crashed.TrySetResult(status);
    }

    private class DisplayObserver : DisplayHandler
    {
        public int CursorCalls;
        public int MediaCalls;
        protected override bool OnCursorChange(CefBrowser browser, IntPtr cursorHandle, CefCursorType type, CefCursorInfo customCursorInfo) { CursorCalls++; return false; }
        protected override void OnMediaAccessChange(CefBrowser browser, bool hasVideoAccess, bool hasAudioAccess) { MediaCalls++; }
    }
}
