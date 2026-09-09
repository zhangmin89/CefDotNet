using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Avalonia.Platform;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Handlers;
using Xilium.CefGlue.Common.InternalHandlers;

namespace CefGlue.Tests;

[TestFixture, NonParallelizable]
public class BrowserReviewFixTests : TestBase
{
    protected static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private Lifecycle lifecycle = null!;
    protected DisplayObserver display = null!;
    protected CefBrowser nativeBrowser = null!;
    private CefBrowserHost nativeHost = null!;
    private protected CommonBrowserAdapter Adapter => (CommonBrowserAdapter)GetField(Browser, "_adapter")!;

    public class Target { public int Echo(int value) => value; }

    public class ArgumentTarget
    {
        public int Calls;
        public int Echo(int value) { System.Threading.Interlocked.Increment(ref Calls); return value; }
    }

    public class OverloadTarget
    {
        public string Load() => "zero";
        public string Load(string url) => "one:" + url;
        public string Load(string url, int timeout) => "two:" + url + ":" + timeout;
        public string Pick(int value) => "fixed:" + value;
        public string Pick(params string[] values) => "params:" + values.Length;
        public Task<int> Sum(int value) => Task.FromResult(value);
        public Task<int> Sum(int value, int other) => Task.FromResult(value + other);
    }

    protected override async Task ExtraSetup()
    {
        lifecycle = new Lifecycle();
        display = new DisplayObserver();
        Browser.LifeSpanHandler = lifecycle;
        Browser.DisplayHandler = display;
        nativeBrowser = (CefBrowser)GetField(Adapter, "_browser")!;
        nativeHost = nativeBrowser.GetHost();
        Assert.AreEqual(WindowlessRenderingEnabled, nativeHost.IsWindowRenderingDisabled, "Run OSR and windowed fixtures in separate test processes.");
        await Run(() =>
        {
            var window = (Window)TopLevel.GetTopLevel(Browser)!;
            window.Width = 180;
            window.Height = 160;
            Browser.Width = 64;
            Browser.Height = 64;
        });
        await Browser.LoadContent("<html><body style='margin:0;width:100vw;height:100vh;background:blue'>test</body></html>").WaitAsync(Deadline);
    }

    [TearDown]
    public async Task CloseNativeBrowsers()
    {
        lifecycle.TakeOverClose = false;
        lifecycle.IgnoreMainClose = false;
        if (lifecycle.Popup?.IsValid == true)
        {
            lifecycle.Popup.GetHost().CloseBrowser(true);
            await lifecycle.PopupClosed.Task.WaitAsync(Deadline);
        }
        if (nativeBrowser.IsValid)
        {
            nativeHost.CloseBrowser(true);
            await lifecycle.MainClosed.Task.WaitAsync(Deadline);
        }
        await CefUi(() => { });
    }

    protected async Task<CefBrowser> OpenPopup(bool windowless)
    {
        lifecycle.WindowlessPopup = windowless;
        await EvaluateJavascript<bool>("document.body.onclick=function(){window.reviewPopup=window.open('about:blank','review-popup','width=64,height=64');}; return true;", Deadline);
        await CefUi(() =>
        {
            nativeHost.SendMouseClickEvent(new CefMouseEvent(10, 10, CefEventFlags.None), CefMouseButtonType.Left, false, 1);
            nativeHost.SendMouseClickEvent(new CefMouseEvent(10, 10, CefEventFlags.None), CefMouseButtonType.Left, true, 1);
        });
        var popup = await lifecycle.PopupCreated.Task.WaitAsync(Deadline);
        using var popupFrame = popup.GetMainFrame();
        popupFrame.ExecuteJavaScript("console.log('review-popup-document-ready');", "", 1);
        await display.Message("review-popup-document-ready").WaitAsync(Deadline);
        return popup;
    }

    [Test]
    public async Task AddressReturnsCurrentMainFrameUrl()
    {
        var expected = await EvaluateJavascript<string>("return window.location.href;", Deadline);
        Assert.IsTrue(expected.StartsWith("data:", StringComparison.Ordinal));
        Assert.AreEqual(expected, Browser.Address);
    }

    [Test]
    public async Task PopupCreatedByUserGestureRemainsAllowed()
    {
        var popup = await OpenPopup(false);
        Assert.IsTrue(popup.IsPopup);
        Assert.IsFalse(popup.GetHost().IsWindowRenderingDisabled);
    }

    [Test]
    public async Task ClosingPopupPreservesMainBrowserAndRegisteredObjects()
    {
        Browser.RegisterJavascriptObject(new Target(), "reviewNative");
        var dispatcher = GetField(Adapter, "_objectMethodDispatcher");
        var popup = await OpenPopup(false);
        popup.GetHost().CloseBrowser(true);
        await lifecycle.PopupClosed.Task.WaitAsync(Deadline);
        await CefUi(() => { });
        Assert.AreSame(nativeBrowser, GetField(Adapter, "_browser"));
        Assert.AreSame(dispatcher, GetField(Adapter, "_objectMethodDispatcher"));
        Assert.IsTrue(Adapter.IsJavascriptObjectRegistered("reviewNative"));
        Browser.ExecuteJavaScript("reviewNative.echo(42).then(value => { window.reviewNativeResult = value; console.log('review-native-result-ready'); });");
        await display.Message("review-native-result-ready").WaitAsync(Deadline);
        Assert.AreEqual(42, await EvaluateJavascript<int>("return window.reviewNativeResult;", Deadline));
    }

    [TestCase("'not-a-number'")]
    [TestCase("")]
    public async Task InvalidNativeArgumentsRejectPromiseWithoutInvokingTarget(string argument)
    {
        var target = new ArgumentTarget();
        Browser.RegisterJavascriptObject(target, "reviewArguments");
        Browser.ExecuteJavaScript($"reviewArguments.echo({argument}).then(value => {{ window.reviewArgumentState = 'resolved'; console.log('review-argument-finished'); }}, error => {{ window.reviewArgumentState = 'rejected'; console.log('review-argument-finished'); }});");
        await display.Message("review-argument-finished").WaitAsync(Deadline);
        Assert.AreEqual("rejected", await EvaluateJavascript<string>("return reviewArgumentState;", Deadline));
        Assert.AreEqual(0, target.Calls);

        Browser.ExecuteJavaScript("reviewArguments.echo(42).then(value => { window.reviewArgumentValue = value; console.log('review-valid-argument-finished'); });");
        await display.Message("review-valid-argument-finished").WaitAsync(Deadline);
        Assert.AreEqual(42, await EvaluateJavascript<int>("return reviewArgumentValue;", Deadline));
        Assert.AreEqual(1, target.Calls);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task JavascriptOverloadsUseArgumentCountsAndPreservePromiseErrors(bool useInterceptor)
    {
        var intercepted = 0;
        Xilium.CefGlue.Common.Events.MethodCallHandler interceptor = action => { System.Threading.Interlocked.Increment(ref intercepted); return action(); };
        Browser.RegisterJavascriptObject(new OverloadTarget(), "reviewOverloads", useInterceptor ? interceptor : null!);
        Browser.ExecuteJavaScript("""
            (async function() {
                const values = [];
                values.push(await reviewOverloads.load());
                values.push(await reviewOverloads.load('page'));
                values.push(await reviewOverloads.load('page', 42));
                values.push(await reviewOverloads.pick(42));
                values.push(await reviewOverloads.pick());
                values.push(await reviewOverloads.pick('a', 'b'));
                values.push(String(await reviewOverloads.sum(40, 2)));
                try { await reviewOverloads.pick('not-a-number'); values.push('accepted'); }
                catch { values.push('rejected'); }
                window.reviewOverloadResults = values;
            })().catch(error => { window.reviewOverloadFailure = String(error); })
                .then(() => console.log('review-overloads-finished'));
            """);
        await display.Message("review-overloads-finished").WaitAsync(Deadline);
        Assert.IsNull(await EvaluateJavascript<string>("return window.reviewOverloadFailure;", Deadline));
        CollectionAssert.AreEqual(new[] { "zero", "one:page", "two:page:42", "fixed:42", "params:0", "params:2", "42", "rejected" }, await EvaluateJavascript<string[]>("return window.reviewOverloadResults;", Deadline));
        Assert.AreEqual(useInterceptor ? 7 : 0, intercepted);
    }

    [Test]
    public async Task PopupDoesNotInheritMainBrowserRegistration()
    {
        Browser.RegisterJavascriptObject(new Target(), "reviewMainOnly");
        Assert.AreEqual("object", await EvaluateJavascript<string>("return typeof reviewMainOnly;", Deadline));
        await OpenPopup(false);
        Assert.AreEqual("undefined", await EvaluateJavascript<string>("return typeof reviewPopup.reviewMainOnly;", Deadline));
        Assert.AreEqual("object", await EvaluateJavascript<string>("return typeof reviewMainOnly;", Deadline));
    }

    [Test]
    public async Task PopupBoundQueryAndUnbindKeepMainBrowserState()
    {
        Browser.RegisterJavascriptObject(new Target(), "reviewMainOnly");
        Assert.AreEqual("object", await EvaluateJavascript<string>("return typeof reviewMainOnly;", Deadline));
        var popup = await OpenPopup(false);
        using var frame = popup.GetMainFrame();
        frame.ExecuteJavaScript("delete window.reviewMainOnly; cefglue.checkObjectBound('reviewMainOnly').then(value => { window.reviewBoundResult = value; console.log('review-popup-bound-finished'); }); cefglue.deleteObjectBound('reviewMainOnly');", "", 1);
        await display.Message("review-popup-bound-finished").WaitAsync(Deadline);
        Assert.IsFalse(await EvaluateJavascript<bool>("return reviewPopup.reviewBoundResult;", Deadline));
        Browser.ExecuteJavaScript("delete window.reviewMainOnly; cefglue.checkObjectBound('reviewMainOnly').then(value => { window.reviewBoundResult = value; console.log('review-main-bound-finished'); });");
        await display.Message("review-main-bound-finished").WaitAsync(Deadline);
        Assert.IsTrue(await EvaluateJavascript<bool>("return reviewBoundResult;", Deadline));
    }

    [Test]
    public async Task PopupConsoleReachesUserHandlerButNotMainControlEvent()
    {
        var popup = await OpenPopup(false);
        var mainEvents = 0;
        Browser.ConsoleMessage += (_, args) => { if (args.Message.StartsWith("review-console-", StringComparison.Ordinal)) mainEvents++; };
        popup.GetMainFrame().ExecuteJavaScript("console.log('review-console-popup');", "", 1);
        Assert.AreEqual(popup.Identifier, await display.Message("review-console-popup").WaitAsync(Deadline));
        await CefUi(() => { });
        Assert.AreEqual(0, mainEvents);
        await EvaluateJavascript<bool>("console.log('review-console-main'); return true;", Deadline);
        Assert.AreEqual(nativeBrowser.Identifier, await display.Message("review-console-main").WaitAsync(Deadline));
        await CefUi(() => { });
        Assert.AreEqual(1, mainEvents);
    }

    [Test]
    public async Task PopupStatusAndLoadNotificationsDoNotRaiseMainControlEvents()
    {
        var popup = await OpenPopup(false);
        var counts = new int[5];
        int[] popupCounts = null!;
        int[] mainCounts = null!;
        Browser.StatusMessage += (_, _) => counts[0]++;
        Browser.LoadStart += (_, _) => counts[1]++;
        Browser.LoadEnd += (_, _) => counts[2]++;
        Browser.LoadError += (_, _) => counts[3]++;
        Browser.LoadingStateChange += (_, _) => counts[4]++;
        var owner = (ICefBrowserHost)Adapter;
        void Notify(CefBrowser browser)
        {
            using var frame = browser.GetMainFrame();
            owner.HandleStatusMessage(browser, "review-status");
            owner.HandleLoadStart(browser, frame, default);
            owner.HandleLoadEnd(browser, frame, 200);
            owner.HandleLoadError(browser, frame, CefErrorCode.Aborted, "review-error", "about:blank");
            owner.HandleLoadingStateChange(browser, false, false, false);
        }
        await CefUi(() =>
        {
            Array.Clear(counts);
            Notify(popup);
            popupCounts = (int[])counts.Clone();
            Array.Clear(counts);
            Notify(nativeBrowser);
            mainCounts = (int[])counts.Clone();
        });
        Assert.AreEqual(new[] { 0, 0, 0, 0, 0 }, popupCounts);
        Assert.AreEqual(new[] { 1, 1, 1, 1, 1 }, mainCounts);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task FinalCloseCallbackCleansResourcesWithoutDefaultDoClose(bool takeOverClose, bool disposeCallbackBrowser)
    {
        Browser.RegisterJavascriptObject(new Target(), "reviewNative");
        var pipe = GetField(Adapter, "_crashServerPipe")!;
        var engine = GetField(Adapter, "_javascriptExecutionEngine")!;
        var proxy = new CommonCefLifeSpanHandler(Adapter);
        lifecycle.TakeOverClose = takeOverClose;
        lifecycle.IgnoreMainClose = true;
        lifecycle.DisposeCloseCallbackBrowser = disposeCallbackBrowser;
        var initializedInUserCallback = false;
        lifecycle.BeforeMainClose = () => initializedInUserCallback = Adapter.IsInitialized;
        try
        {
            await CefUi(() =>
            {
                using var callbackBrowser = nativeHost.GetBrowser();
                if (takeOverClose)
                {
                    Assert.IsTrue((bool)Invoke(proxy, "DoClose", callbackBrowser)!);
                    Assert.IsTrue(Adapter.IsInitialized, "The user's DoClose=true must retain control of closing.");
                }
                // Exercise the library's real final-close proxy with owned resources;
                // this is a controlled callback, not a claim that returning true closes CEF.
                Invoke(proxy, "OnBeforeClose", callbackBrowser);
            });
            Assert.IsTrue(initializedInUserCallback, "The user callback must still precede owner cleanup.");
            Assert.IsNull(GetField(Adapter, "_browser"));
            Assert.IsNull(GetField(Adapter, "_objectMethodDispatcher"));
            Assert.AreEqual(1, GetField(pipe, "_disposed"));
            Assert.IsNull(GetField(engine, "ContextCreated"));
            Assert.IsFalse(Adapter.IsJavascriptObjectRegistered("reviewNative"));
        }
        finally
        {
            lifecycle.BeforeMainClose = null;
            lifecycle.DisposeCloseCallbackBrowser = false;
            lifecycle.TakeOverClose = false;
            lifecycle.IgnoreMainClose = false;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CursorCallbackPreservesUserHandledResult(bool handled)
    {
        display.CursorHandled = handled;
        var proxy = new CommonCefDisplayHandler(Adapter);
        var result = false;
        var calls = 0;
        await CefUi(() =>
        {
            display.CursorCalls = 0;
            result = (bool)Invoke(proxy, "OnCursorChange", nativeBrowser, IntPtr.Zero, CefCursorType.Hand, null!)!;
            calls = display.CursorCalls;
        });
        Assert.AreEqual(1, calls);
        Assert.AreEqual(handled || WindowlessRenderingEnabled, result);
    }

    [Test]
    public async Task MediaCallbackPreservesAccessFlags()
    {
        var proxy = new CommonCefDisplayHandler(Adapter);
        await CefUi(() =>
        {
            display.MediaCalls = 0;
            Invoke(proxy, "OnMediaAccessChange", nativeBrowser, true, false);
        });
        Assert.AreEqual(1, display.MediaCalls);
        Assert.IsTrue(display.VideoAccess);
        Assert.IsFalse(display.AudioAccess);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SurfaceDisposeClearsImageBeforeReleasingBitmap(bool workerThread)
    {
        Image image = null!;
        AvaloniaRenderSurface surface = null!;
        await Run(() =>
        {
            image = new Image();
            surface = new AvaloniaRenderSurface(image);
            Invoke(surface, "CreateBitmap", 8, 8);
            image.Measure(new Size(8, 8));
            image.Arrange(new Rect(0, 0, 8, 8));
        });
        if (workerThread) await Task.Run(surface.Dispose);
        else await Run(surface.Dispose);
        await Run(() =>
        {
            Assert.IsNull(image.Source);
            using var render = new RenderTargetBitmap(new PixelSize(8, 8));
            Assert.DoesNotThrow(() => render.Render(image));
            Assert.DoesNotThrow(surface.Dispose);
        });
    }

    [Test]
    public async Task PopupCanReopenUntilDisposeClosesItsWindow()
    {
        ExtendedAvaloniaPopup popup = null!;
        AvaloniaPopup host = null!;
        var closed = false;
        await Run(() =>
        {
            popup = new ExtendedAvaloniaPopup { PlacementTarget = Browser, Width = 16, Height = 16, ShowInTaskbar = false };
            popup.Closed += (_, _) => closed = true;
            host = new AvaloniaPopup(popup, popup.VisualChildren);
            host.Open();
        });
        try
        {
            await Run(() => { Assert.IsTrue(popup.IsVisible); host.Close(); });
            await Run(() => { Assert.IsFalse(popup.IsVisible); Assert.IsFalse(closed); host.Open(); });
            await Run(() => { Assert.IsTrue(popup.IsVisible); host.Dispose(); });
            await Run(() => { Assert.IsTrue(closed); Assert.DoesNotThrow(host.Dispose); });
        }
        finally
        {
            await Run(() => { host.Dispose(); host.RenderSurface.Dispose(); popup.Close(); });
        }
    }

    [TestCase(KeyModifiers.Control | KeyModifiers.Shift, CefEventFlags.ControlDown | CefEventFlags.ShiftDown)]
    [TestCase(KeyModifiers.Meta | KeyModifiers.Shift, CefEventFlags.CommandDown | CefEventFlags.ShiftDown)]
    [TestCase(KeyModifiers.Alt, CefEventFlags.AltDown)]
    [TestCase(KeyModifiers.None, CefEventFlags.None)]
    public async Task SmoothWheelPreservesDeltaAndKeyboardModifiers(KeyModifiers modifiers, CefEventFlags keyboardFlags)
    {
        var x = 0;
        var y = 0;
        var flags = CefEventFlags.None;
        var keyFlags = CefEventFlags.None;
        await Run(() =>
        {
            var control = new ExtendedAvaloniaPopup { ShowInTaskbar = false };
            var host = new AvaloniaOffScreenControlHost(control, control.VisualChildren);
            try
            {
                var pointer = new global::Avalonia.Input.Pointer(1, PointerType.Mouse, true);
                var wheel = new PointerWheelEventArgs(control, pointer, control, new Point(), 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), modifiers, new Vector(0.25, -0.5));
                host.MouseWheelChanged += (mouse, dx, dy) => { x = dx; y = dy; flags = mouse.Modifiers; };
                Invoke(host, "OnPointerWheelChanged", control, wheel);
                keyFlags = new KeyEventArgs { Key = Key.A, KeyModifiers = modifiers }.AsCefKeyEvent(false).Modifiers;
            }
            finally { host.RenderSurface.Dispose(); control.Close(); }
        });
        Assert.AreEqual(25, x);
        Assert.AreEqual(-50, y);
        Assert.AreEqual(keyboardFlags | CefEventFlags.LeftMouseButton, flags);
        Assert.AreEqual(keyboardFlags, keyFlags);
    }

    [Test]
    public async Task DragLeaveRestoresOriginalCursor()
    {
        await Run(() =>
        {
            var control = new ExtendedAvaloniaPopup { ShowInTaskbar = false };
            var host = new AvaloniaOffScreenControlHost(control, control.VisualChildren);
            using var original = new Cursor(StandardCursorType.Arrow);
            using var drag = new Cursor(StandardCursorType.Cross);
            try
            {
                typeof(AvaloniaOffScreenControlHost).GetField("_previousCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, original);
                control.Cursor = drag;
                Invoke(host, "OnDragLeave", control, new RoutedEventArgs());
                Assert.AreSame(original, control.Cursor);
            }
            finally { host.RenderSurface.Dispose(); control.Close(); }
        });
    }

    protected static uint CenterPixel(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        return unchecked((uint)Marshal.ReadInt32(buffer.Address, buffer.RowBytes * (buffer.Size.Height / 2) + 4 * (buffer.Size.Width / 2)));
    }

    protected static object? GetField(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target);
        }
        throw new MissingFieldException(target.GetType().FullName, name);
    }

    protected static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    protected static Task CefUi(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = CefRuntime.PostTask(CefThreadId.UI, new CallbackTask(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }));
        Assert.IsTrue(posted);
        return completion.Task.WaitAsync(Deadline);
    }

    private sealed class CallbackTask : CefTask
    {
        private readonly Action action;
        public CallbackTask(Action action) => this.action = action;
        protected override void Execute() => action();
    }

    private sealed class Lifecycle : LifeSpanHandler
    {
        public readonly TaskCompletionSource<CefBrowser> PopupCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource PopupClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource MainClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CefBrowser? Popup;
        public bool WindowlessPopup;
        public bool TakeOverClose;
        public bool IgnoreMainClose;
        public bool DisposeCloseCallbackBrowser;
        public Action? BeforeMainClose;
        protected override void OnAfterCreated(CefBrowser browser) { if (browser.IsPopup) { Popup = browser; PopupCreated.TrySetResult(browser); } }
        protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, string targetUrl, string targetFrameName, CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
        {
            if (WindowlessPopup) windowInfo.SetAsWindowless(IntPtr.Zero, false);
            return false;
        }
        protected override bool DoClose(CefBrowser browser) => TakeOverClose;
        protected override void OnBeforeClose(CefBrowser browser)
        {
            if (browser.IsPopup) PopupClosed.TrySetResult();
            else { BeforeMainClose?.Invoke(); if (!IgnoreMainClose) MainClosed.TrySetResult(); }
            if (DisposeCloseCallbackBrowser) browser.Dispose();
        }
    }

    protected sealed class DisplayObserver : DisplayHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<int>> messages = new();
        public bool CursorHandled;
        public int CursorCalls;
        public int MediaCalls;
        public bool VideoAccess;
        public bool AudioAccess;
        public Task<int> Message(string text) => messages.GetOrAdd(text, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        protected override bool OnConsoleMessage(CefBrowser browser, CefLogSeverity level, string message, string source, int line)
        {
            messages.GetOrAdd(message, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(browser.Identifier);
            return false;
        }
        protected override bool OnCursorChange(CefBrowser browser, IntPtr cursorHandle, CefCursorType type, CefCursorInfo customCursorInfo) { CursorCalls++; return CursorHandled; }
        protected override void OnMediaAccessChange(CefBrowser browser, bool hasVideoAccess, bool hasAudioAccess) { MediaCalls++; VideoAccess = hasVideoAccess; AudioAccess = hasAudioAccess; }
    }
}
