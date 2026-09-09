using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Handlers;
using Xilium.CefGlue.Common.Helpers;
using Xilium.CefGlue.Common.Helpers.Logger;
using Xilium.CefGlue.Common.JavascriptExecution;
using Xilium.CefGlue.Common.Platform;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;
using Xilium.CefGlue.Interop;

namespace CefGlue.Tests;

[TestFixture, NonParallelizable]
public class RequestHandlerReviewFixTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase(false)]
    [TestCase(true)]
    public async Task TerminationCancelsOnlyItsBrowserBeforeCallingUserHandler(bool disposeCallbackBrowser)
    {
        CefRuntime.Load();
        using var native = new NativeRequests();
        using var fixture = new Fixture();
        using var tracking = CefObjectTracker.StartTracking();
        var firstBrowser = native.Browser(1);
        var otherBrowser = native.Browser(2);
        var firstFrame = native.Frame(firstBrowser, 11);
        var otherFrame = native.Frame(otherBrowser, 22);
        var first = fixture.Engine.Evaluate<int>("return 7;", "", 1, firstFrame);
        var other = fixture.Engine.Evaluate<int>("return 42;", "", 1, otherFrame);
        var pendingAtCallback = -1;
        var user = new RecordingHandler(false)
        {
            Terminated = browser =>
            {
                pendingAtCallback = fixture.Pending.Count;
                if (disposeCallbackBrowser) browser.Dispose();
            }
        };
        fixture.Adapter.RequestHandler = user;
        try
        {
            using var callbackBrowser = native.Copy(firstBrowser);
            Invoke(fixture.Handler!, "OnRenderProcessTerminated", callbackBrowser, CefTerminationStatus.ProcessCrashed);
            Assert.AreEqual(1, pendingAtCallback, "Internal cleanup must precede a user override that omits base.");
            Assert.AreEqual(0, await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(other.IsCompleted, "The other browser's request must remain pending.");
            Reply(fixture, otherBrowser, otherFrame, native.Sent.Single(item => item.FrameId == 22).Request.TaskId, "42");
            Assert.AreEqual(42, await other.WaitAsync(TimeSpan.FromSeconds(10)));
            var recovered = fixture.Engine.Evaluate<int>("return 42;", "", 1, firstFrame);
            Reply(fixture, firstBrowser, firstFrame, native.Sent.Last().Request.TaskId, "42");
            Assert.AreEqual(42, await recovered.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(0, fixture.Pending.Count);
            Assert.AreEqual("OnRenderProcessTerminated", user.Method);
            Assert.IsNull(native.CallbackError);
        }
        finally
        {
            fixture.Engine.Dispose();
            await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UserCallbacksKeepArgumentsDecisionsRefValuesAndOwnership(bool decision)
    {
        CefRuntime.Load();
        using var native = new NativeRequests();
        using var fixture = new Fixture();
        var user = new RecordingHandler(decision);
        fixture.Adapter.RequestHandler = user;
        var handler = fixture.Handler!;
        foreach (var invocation in Calls(native))
        {
            var arguments = (object?[])invocation.Arguments.Clone();
            var references = native.References;
            var result = Invoke(handler, invocation.Method, arguments);
            Assert.AreEqual(invocation.Method, user.Method);
            CollectionAssert.AreEqual(invocation.Arguments, user.Arguments, "Arguments must reach the user unchanged.");
            Assert.AreEqual(references, native.References, "Forwarding must not dispose callback or browser wrappers.");
            if (invocation.Method == "GetResourceRequestHandler")
            {
                Assert.AreSame(user.Resource, result);
                Assert.AreEqual(decision, arguments[6]);
            }
            else if (invocation.ReturnsBoolean) Assert.AreEqual(decision, result);
            else Assert.IsNull(result);
        }
        var replacement = new RecordingHandler(!decision);
        fixture.Adapter.RequestHandler = replacement;
        var beforeBrowse = Calls(native).First();
        Assert.AreEqual(!decision, Invoke(fixture.Handler!, beforeBrowse.Method, beforeBrowse.Arguments));
        Assert.AreEqual("OnBeforeBrowse", replacement.Method, "Replacing the public handler must take effect immediately.");
    }

    [Test]
    public void MissingUserHandlerKeepsCefDefaultsAndTheTerminationHook()
    {
        CefRuntime.Load();
        using var native = new NativeRequests();
        using var fixture = new Fixture();
        var handler = fixture.Handler;
        Assert.IsNotNull(handler, "The library still needs a termination callback without a user handler.");
        foreach (var invocation in Calls(native))
        {
            var arguments = (object?[])invocation.Arguments.Clone();
            var result = Invoke(handler!, invocation.Method, arguments);
            if (invocation.ReturnsBoolean) Assert.AreEqual(false, result);
            else Assert.IsNull(result);
            if (invocation.Method == "GetResourceRequestHandler") Assert.AreEqual(true, arguments[6]);
        }
    }

    private static void Reply(Fixture fixture, CefBrowser browser, CefFrame frame, int taskId, string json)
    {
        using var response = new Messages.JsEvaluationResult { TaskId = taskId, Success = true, ResultAsJson = json }.ToCefProcessMessage();
        fixture.Client.Dispatcher.DispatchMessage(browser, frame, CefProcessId.Renderer, response);
    }

    private static object? Invoke(CefRequestHandler handler, string method, params object?[] arguments) => typeof(CefRequestHandler).GetMethod(method, PrivateInstance)!.Invoke(handler, arguments);

    private sealed record Invocation(string Method, object?[] Arguments, bool ReturnsBoolean = false);

    private static Invocation[] Calls(NativeRequests native)
    {
        var browser = native.Browser(3);
        var frame = native.Frame(browser, 33);
        var request = native.Request();
        return new[]
        {
            new Invocation("OnBeforeBrowse", new object?[] { browser, frame, request, true, false }, true),
            new Invocation("OnOpenUrlFromTab", new object?[] { browser, frame, "https://review.invalid/page", CefWindowOpenDisposition.NewForegroundTab, true }, true),
            new Invocation("GetResourceRequestHandler", new object?[] { browser, frame, request, true, false, "https://review.invalid", true }),
            new Invocation("GetAuthCredentials", new object?[] { browser, "https://review.invalid", true, "review.invalid", 8443, "review-realm", "basic", native.AuthCallback() }, true),
            new Invocation("OnCertificateError", new object?[] { browser, CefErrorCode.CertAuthorityInvalid, "https://review.invalid", native.SslInfo(), native.Callback() }, true),
            new Invocation("OnSelectClientCertificate", new object?[] { browser, false, "review.invalid", 443, new[] { native.Certificate() }, native.SelectCertificateCallback() }, true),
            new Invocation("OnRenderViewReady", new object?[] { browser }),
            new Invocation("OnRenderProcessTerminated", new object?[] { browser, CefTerminationStatus.ProcessCrashed }),
            new Invocation("OnDocumentAvailableInMainFrame", new object?[] { browser })
        };
    }

    private sealed class RecordingHandler : RequestHandler
    {
        private readonly bool _decision;
        public string? Method;
        public object?[]? Arguments;
        public Action<CefBrowser>? Terminated;
        public CefResourceRequestHandler? Resource { get; }
        public RecordingHandler(bool decision) { _decision = decision; Resource = decision ? new ResourceHandler() : null; }
        private void Record(string method, params object?[] arguments) { Method = method; Arguments = arguments; }
        protected override bool OnBeforeBrowse(CefBrowser browser, CefFrame frame, CefRequest request, bool userGesture, bool isRedirect)
        { Record(nameof(OnBeforeBrowse), browser, frame, request, userGesture, isRedirect); return _decision; }
        protected override bool OnOpenUrlFromTab(CefBrowser browser, CefFrame frame, string targetUrl, CefWindowOpenDisposition targetDisposition, bool userGesture)
        { Record(nameof(OnOpenUrlFromTab), browser, frame, targetUrl, targetDisposition, userGesture); return _decision; }
        protected override CefResourceRequestHandler GetResourceRequestHandler(CefBrowser browser, CefFrame frame, CefRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        { Record(nameof(GetResourceRequestHandler), browser, frame, request, isNavigation, isDownload, requestInitiator, disableDefaultHandling); disableDefaultHandling = _decision; return Resource!; }
        protected override bool GetAuthCredentials(CefBrowser browser, string originUrl, bool isProxy, string host, int port, string realm, string scheme, CefAuthCallback callback)
        { Record(nameof(GetAuthCredentials), browser, originUrl, isProxy, host, port, realm, scheme, callback); return _decision; }
        protected override bool OnCertificateError(CefBrowser browser, CefErrorCode certError, string requestUrl, CefSslInfo sslInfo, CefCallback callback)
        { Record(nameof(OnCertificateError), browser, certError, requestUrl, sslInfo, callback); return _decision; }
        protected override bool OnSelectClientCertificate(CefBrowser browser, bool isProxy, string host, int port, CefX509Certificate[] certificates, CefSelectClientCertificateCallback callback)
        { Record(nameof(OnSelectClientCertificate), browser, isProxy, host, port, certificates, callback); return _decision; }
        protected override void OnRenderViewReady(CefBrowser browser) => Record(nameof(OnRenderViewReady), browser);
        protected override void OnRenderProcessTerminated(CefBrowser browser, CefTerminationStatus status)
        { Record(nameof(OnRenderProcessTerminated), browser, status); Terminated?.Invoke(browser); }
        protected override void OnDocumentAvailableInMainFrame(CefBrowser browser) => Record(nameof(OnDocumentAvailableInMainFrame), browser);
    }

    private sealed class ResourceHandler : CefResourceRequestHandler
    {
        protected override CefCookieAccessFilter GetCookieAccessFilter(CefBrowser browser, CefFrame frame, CefRequest request) => null!;
    }

    private sealed class Fixture : IDisposable
    {
        public CommonBrowserAdapter Adapter { get; } = new(new object(), "review-request", new Control(), new NullLogger());
        public CommonCefClient Client { get; }
        public JavascriptExecutionEngine Engine { get; }
        public Fixture()
        {
            Client = new CommonCefClient(Adapter, null!, new NullLogger());
            Engine = new JavascriptExecutionEngine(Client.Dispatcher);
            typeof(CommonBrowserAdapter).GetField("_javascriptExecutionEngine", PrivateInstance)!.SetValue(Adapter, Engine);
        }
        public CefRequestHandler? Handler => (CefRequestHandler?)typeof(CommonCefClient).GetMethod("GetRequestHandler", PrivateInstance)!.Invoke(Client, null);
        public IDictionary Pending => (IDictionary)typeof(JavascriptExecutionEngine).GetField("_pendingTasks", PrivateInstance)!.GetValue(Engine)!;
        public void Dispose() { Engine.Dispose(); Adapter.Dispose(); }
    }

    private sealed class Control : IControl
    {
        public event Action GotFocus { add { } remove { } }
        public event Action<CefSize> SizeChanged { add { } remove { } }
        public IntPtr? GetHostViewHandle(int initialWidth, int initialHeight) => null;
        public void OpenContextMenu(IEnumerable<MenuEntry> menuEntries, int x, int y, CefRunContextMenuCallback callback) { }
        public void CloseContextMenu() { }
        public void SetTooltip(string text) { }
        public void InitializeRender(IntPtr browserHandle) { }
        public void DestroyRender() { }
        public bool SetCursor(IntPtr cursorHandle, CefCursorType cursorType) => false;
    }

    // Only the native functions used by the engine or reference ownership are provided.
    private unsafe sealed class NativeRequests : IDisposable
    {
        private sealed class Slot
        {
            public nint Address;
            public int References = 1;
            public long Identifier;
            public Slot? Browser;
            public required NativeRequests Owner;
        }
        private static readonly ConcurrentDictionary<nint, Slot> Slots = new();
        private readonly List<Slot> _owned = new();
        private readonly Dictionary<IDisposable, Slot> _wrappers = new();
        public readonly List<(long FrameId, Messages.JsEvaluationRequest Request)> Sent = new();
        public Exception? CallbackError;
        public int References => _owned.Sum(slot => slot.References);
        private Slot Add<T>() where T : unmanaged
        {
            var pointer = (T*)Marshal.AllocHGlobal(sizeof(T));
            *pointer = default;
            var slot = new Slot { Address = (nint)pointer, Owner = this };
            var counted = (cef_base_ref_counted_t*)pointer;
            counted->_size = (UIntPtr)(uint)sizeof(T);
            counted->_add_ref = (nint)(delegate* unmanaged<void*, void>)&AddRef;
            counted->_release = (nint)(delegate* unmanaged<void*, int>)&Release;
            counted->_has_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasOneRef;
            counted->_has_at_least_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasAnyRef;
            Slots[slot.Address] = slot;
            _owned.Add(slot);
            return slot;
        }
        private T Keep<T>(T wrapper, Slot slot) where T : IDisposable { _wrappers.Add(wrapper, slot); return wrapper; }
        public CefBrowser Browser(int identifier)
        {
            var slot = Add<cef_browser_t>();
            slot.Identifier = identifier;
            ((cef_browser_t*)slot.Address)->_get_identifier = (nint)(delegate* unmanaged<cef_browser_t*, int>)&GetBrowserIdentifier;
            return Keep(CefBrowser.FromNative((cef_browser_t*)slot.Address), slot);
        }
        public CefBrowser Copy(CefBrowser browser)
        {
            var slot = _wrappers[browser];
            Interlocked.Increment(ref slot.References);
            return Keep(CefBrowser.FromNative((cef_browser_t*)slot.Address), slot);
        }
        public CefFrame Frame(CefBrowser browser, long identifier)
        {
            var slot = Add<cef_frame_t>();
            slot.Browser = _wrappers[browser];
            slot.Identifier = identifier;
            var pointer = (cef_frame_t*)slot.Address;
            pointer->_get_identifier = (nint)(delegate* unmanaged<cef_frame_t*, long>)&GetFrameIdentifier;
            pointer->_get_browser = (nint)(delegate* unmanaged<cef_frame_t*, cef_browser_t*>)&GetBrowser;
            pointer->_send_process_message = (nint)(delegate* unmanaged<cef_frame_t*, CefProcessId, cef_process_message_t*, void>)&Send;
            return Keep(CefFrame.FromNative(pointer), slot);
        }
        public CefRequest Request() { var slot = Add<cef_request_t>(); return Keep(CefRequest.FromNative((cef_request_t*)slot.Address), slot); }
        public CefAuthCallback AuthCallback() { var slot = Add<cef_auth_callback_t>(); return Keep(CefAuthCallback.FromNative((cef_auth_callback_t*)slot.Address), slot); }
        public CefCallback Callback() { var slot = Add<cef_callback_t>(); return Keep(CefCallback.FromNative((cef_callback_t*)slot.Address), slot); }
        public CefSslInfo SslInfo() { var slot = Add<cef_sslinfo_t>(); return Keep(CefSslInfo.FromNative((cef_sslinfo_t*)slot.Address), slot); }
        public CefX509Certificate Certificate() { var slot = Add<cef_x509certificate_t>(); return Keep(CefX509Certificate.FromNative((cef_x509certificate_t*)slot.Address), slot); }
        public CefSelectClientCertificateCallback SelectCertificateCallback() { var slot = Add<cef_select_client_certificate_callback_t>(); return Keep(CefSelectClientCertificateCallback.FromNative((cef_select_client_certificate_callback_t*)slot.Address), slot); }
        public void Dispose()
        {
            foreach (var wrapper in _wrappers.Keys) wrapper.Dispose();
            foreach (var slot in _owned) Assert.AreEqual(0, slot.References, "Unbalanced native reference ownership.");
            foreach (var slot in _owned) { Slots.TryRemove(slot.Address, out _); Marshal.FreeHGlobal(slot.Address); }
        }
        [UnmanagedCallersOnly]
        private static void AddRef(void* pointer) => Interlocked.Increment(ref Slots[(nint)pointer].References);
        [UnmanagedCallersOnly]
        private static int Release(void* pointer) => Interlocked.Decrement(ref Slots[(nint)pointer].References) == 0 ? 1 : 0;
        [UnmanagedCallersOnly]
        private static int HasOneRef(void* pointer) => Slots[(nint)pointer].References == 1 ? 1 : 0;
        [UnmanagedCallersOnly]
        private static int HasAnyRef(void* pointer) => Slots[(nint)pointer].References > 0 ? 1 : 0;
        [UnmanagedCallersOnly]
        private static int GetBrowserIdentifier(cef_browser_t* pointer) => (int)Slots[(nint)pointer].Identifier;
        [UnmanagedCallersOnly]
        private static long GetFrameIdentifier(cef_frame_t* pointer) => Slots[(nint)pointer].Identifier;
        [UnmanagedCallersOnly]
        private static cef_browser_t* GetBrowser(cef_frame_t* pointer)
        {
            var browser = Slots[(nint)pointer].Browser!;
            Interlocked.Increment(ref browser.References);
            return (cef_browser_t*)browser.Address;
        }
        [UnmanagedCallersOnly]
        private static void Send(cef_frame_t* pointer, CefProcessId target, cef_process_message_t* message)
        {
            var slot = Slots[(nint)pointer];
            try
            {
                using var managed = CefProcessMessage.FromNative(message);
                slot.Owner.Sent.Add((slot.Identifier, Messages.JsEvaluationRequest.FromCefMessage(managed)));
            }
            catch (Exception exception) { slot.Owner.CallbackError = exception; }
        }
    }
}
