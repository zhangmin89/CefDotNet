using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Shared.Helpers;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;
using Xilium.CefGlue.Interop;

namespace CefGlue.Tests;

[TestFixture, NonParallelizable]
public class RendererBrowserStateReviewFixTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Assembly Renderer => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Xilium.CefGlue.BrowserProcess.dll"));

    [TestCase("null-extra")]
    [TestCase("empty-extra")]
    [TestCase("replacement")]
    [TestCase("owner-closed")]
    public async Task PopupWithoutCrashPipeKeepsTheParentReportRoute(string mode)
    {
        CefRuntime.Load();
        using var native = new NativeBrowsers();
        using var renderer = new RendererHandler();
        var main = native.Create(7);
        var pipeName = "cef-review-route-" + Guid.NewGuid().ToString("N");
        using var extra = CefDictionaryValue.Create();
        extra.SetString(Constants.CrashPipeNameKey, pipeName);
        renderer.Create(main, extra);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new PipeServer(pipeName);
        server.MessageReceived += message => received.TrySetResult(message);
        if (mode == "owner-closed")
        {
            renderer.Destroy(native.Copy(main));
            Assert.IsNull(renderer.Browser);
            Assert.IsTrue(string.IsNullOrEmpty(renderer.PipeName), "The report pipe must be cleared with its browser owner.");
            return;
        }

        var popup = native.Create(8);
        using var popupExtra = mode == "null-extra" ? null : CefDictionaryValue.Create();
        if (mode == "replacement") popupExtra!.SetString(Constants.CrashPipeNameKey, pipeName);
        renderer.Create(popup, popupExtra);
        Assert.AreSame(mode == "replacement" ? popup : main, renderer.Browser);
        Assert.AreEqual(pipeName, renderer.PipeName);
        renderer.Send(new InvalidOperationException("review route retained"));
        var report = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        StringAssert.Contains("review route retained", report);
        StringAssert.Contains(nameof(InvalidOperationException), report);
        if (mode != "replacement")
        {
            renderer.Destroy(native.Copy(popup));
            Assert.AreSame(main, renderer.Browser, "Closing an unrelated popup must retain the report owner.");
            Assert.AreEqual(pipeName, renderer.PipeName);
        }
    }

    [Test]
    public void SameNameRegistrationsAndUnregistrationAreIndependent()
    {
        CefRuntime.Load();
        using var native = new NativeBrowsers();
        using var renderer = new RendererHandler();
        using var extra = CefDictionaryValue.Create();
        extra.SetString(Constants.CrashPipeNameKey, "review-state-" + Guid.NewGuid().ToString("N"));
        var first = native.Create(17);
        renderer.Create(first, extra);
        Register(renderer.Dispatcher, first, "shared", "alpha");
        var second = native.Create(18);
        renderer.Create(second, extra);
        Register(renderer.Dispatcher, second, "shared", "beta");
        CollectionAssert.AreEquivalent(new[] { "alpha", "beta" }, RegisteredMethods(renderer.Dispatcher));
        Assert.AreEqual(2, Queries(renderer.Dispatcher).Length, "Each browser needs its own pending bound query.");
        using var firstCallback = native.Copy(first);
        using var message = new Messages.NativeObjectUnregistrationRequest { ObjectName = "shared" }.ToCefProcessMessage();
        renderer.Dispatcher.GetType().GetMethod("HandleNativeObjectUnregistration", PrivateInstance)!.Invoke(renderer.Dispatcher, new object[] { new MessageReceivedEventArgs(firstCallback, null!, CefProcessId.Browser, message) });
        CollectionAssert.AreEqual(new[] { "beta" }, RegisteredMethods(renderer.Dispatcher));
        Assert.AreEqual(1, Queries(renderer.Dispatcher).Length);
        Assert.IsFalse(Queries(renderer.Dispatcher).Single().Task.IsCompleted);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BrowserDestructionClearsOnlyItsStateAfterOverlappingInstances(bool overlap)
    {
        CefRuntime.Load();
        using var native = new NativeBrowsers();
        using var renderer = new RendererHandler();
        using var extra = CefDictionaryValue.Create();
        extra.SetString(Constants.CrashPipeNameKey, "review-state-" + Guid.NewGuid().ToString("N"));
        var first = native.Create(17);
        renderer.Create(first, extra);
        Register(renderer.Dispatcher, first, "shared", "alpha");
        var pending = Queries(renderer.Dispatcher).Single();
        var current = first;
        if (overlap)
        {
            current = native.Create(17);
            renderer.Create(current, extra);
            renderer.Destroy(native.Copy(first));
            CollectionAssert.AreEqual(new[] { "alpha" }, RegisteredMethods(renderer.Dispatcher));
            Assert.IsFalse(pending.Task.IsCompleted, "The replacement browser still owns this logical browser's state.");
        }
        var other = native.Create(18);
        renderer.Create(other, extra);
        Register(renderer.Dispatcher, other, "other", "otherMethod");
        renderer.Destroy(native.Copy(current));
        CollectionAssert.AreEqual(new[] { "otherMethod" }, RegisteredMethods(renderer.Dispatcher));
        Assert.IsTrue(pending.Task.IsCanceled);
        Assert.AreEqual(1, Queries(renderer.Dispatcher).Length);
        var recreated = native.Create(17);
        renderer.Create(recreated, extra);
        Register(renderer.Dispatcher, recreated, "shared", "beta");
        CollectionAssert.AreEquivalent(new[] { "beta", "otherMethod" }, RegisteredMethods(renderer.Dispatcher));
    }

    private static void Register(object dispatcher, CefBrowser browser, string name, string method)
    {
        using var message = new Messages.NativeObjectRegistrationRequest { ObjectName = name, MethodsNames = new[] { method } }.ToCefProcessMessage();
        dispatcher.GetType().GetMethod("HandleNativeObjectRegistration", PrivateInstance)!.Invoke(dispatcher, new object[] { new MessageReceivedEventArgs(browser, null!, CefProcessId.Browser, message) });
    }

    private static string[] RegisteredMethods(object dispatcher)
    {
        var objects = (System.Collections.IDictionary)dispatcher.GetType().GetField("_registeredObjects", PrivateInstance)!.GetValue(dispatcher)!;
        return objects.Values.Cast<object>().SelectMany(info => (string[])info.GetType().GetProperty("MethodsNames")!.GetValue(info)!).ToArray();
    }

    private static TaskCompletionSource<bool>[] Queries(object dispatcher)
    {
        var queries = (System.Collections.IDictionary)dispatcher.GetType().GetField("_pendingBoundQueryTasks", PrivateInstance)!.GetValue(dispatcher)!;
        return queries.Values.Cast<TaskCompletionSource<bool>>().ToArray();
    }

    private static object CreateDispatcher()
    {
        var type = Renderer.GetType("Xilium.CefGlue.BrowserProcess.ObjectBinding.JavascriptToNativeDispatcherRenderSide", true)!;
        var dispatcher = RuntimeHelpers.GetUninitializedObject(type);
        foreach (var field in type.GetFields(PrivateInstance)) field.SetValue(dispatcher, Activator.CreateInstance(field.FieldType));
        return dispatcher;
    }

    private sealed class RendererHandler : IDisposable
    {
        private readonly Type _type = Renderer.GetType("Xilium.CefGlue.BrowserProcess.Handlers.RenderProcessHandler", true)!;
        private readonly object _handler;
        private readonly UnhandledExceptionEventHandler _subscription;
        public RendererHandler()
        {
            _handler = Activator.CreateInstance(_type)!;
            _subscription = (UnhandledExceptionEventHandler)_type.GetMethod("OnUnhandledException", PrivateInstance)!.CreateDelegate(typeof(UnhandledExceptionEventHandler), _handler);
            _type.GetField("_javascriptToNativeDispatcher", PrivateInstance)!.SetValue(_handler, CreateDispatcher());
        }
        public object Dispatcher => _type.GetField("_javascriptToNativeDispatcher", PrivateInstance)!.GetValue(_handler)!;
        public CefBrowser? Browser => (CefBrowser?)_type.GetField("_browser", PrivateInstance)!.GetValue(_handler);
        public string? PipeName => (string?)_type.GetField("_crashPipeName", PrivateInstance)!.GetValue(_handler);
        public void Create(CefBrowser browser, CefDictionaryValue? extra) => _type.GetMethod("OnBrowserCreated", PrivateInstance)!.Invoke(_handler, new object?[] { browser, extra });
        public void Destroy(CefBrowser browser) => _type.GetMethod("OnBrowserDestroyed", PrivateInstance)!.Invoke(_handler, new object[] { browser });
        public void Send(Exception exception) => _type.GetMethod("SendExceptionToParentProcess", PrivateInstance)!.Invoke(_handler, new object[] { exception });
        public void Dispose()
        {
            AppDomain.CurrentDomain.UnhandledException -= _subscription;
            Browser?.Dispose();
        }
    }

    private unsafe sealed class NativeBrowsers : IDisposable
    {
        private sealed class Slot
        {
            public nint Address;
            public int References;
            public int Identifier;
        }
        private static readonly ConcurrentDictionary<nint, Slot> Slots = new();
        private readonly List<Slot> _owned = new();
        private readonly Dictionary<CefBrowser, Slot> _wrappers = new();
        public CefBrowser Create(int identifier)
        {
            var pointer = (cef_browser_t*)Marshal.AllocHGlobal(sizeof(cef_browser_t));
            *pointer = default;
            var slot = new Slot { Address = (nint)pointer, Identifier = identifier, References = 1 };
            pointer->_base._size = (UIntPtr)(uint)sizeof(cef_browser_t);
            pointer->_base._add_ref = (nint)(delegate* unmanaged<void*, void>)&AddRef;
            pointer->_base._release = (nint)(delegate* unmanaged<void*, int>)&Release;
            pointer->_base._has_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasOneRef;
            pointer->_base._has_at_least_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasAnyRef;
            pointer->_get_identifier = (nint)(delegate* unmanaged<cef_browser_t*, int>)&GetIdentifier;
            pointer->_is_same = (nint)(delegate* unmanaged<cef_browser_t*, cef_browser_t*, int>)&IsSame;
            pointer->_get_main_frame = (nint)(delegate* unmanaged<cef_browser_t*, cef_frame_t*>)&GetMainFrame;
            Slots[slot.Address] = slot;
            _owned.Add(slot);
            var wrapper = CefBrowser.FromNative(pointer);
            _wrappers.Add(wrapper, slot);
            return wrapper;
        }
        public CefBrowser Copy(CefBrowser browser)
        {
            var slot = _wrappers[browser];
            Interlocked.Increment(ref slot.References);
            var wrapper = CefBrowser.FromNative((cef_browser_t*)slot.Address);
            _wrappers.Add(wrapper, slot);
            return wrapper;
        }
        public void Dispose()
        {
            foreach (var wrapper in _wrappers.Keys) wrapper.Dispose();
            foreach (var slot in _owned) Assert.AreEqual(0, slot.References, "Browser reference was not released.");
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
        private static int GetIdentifier(cef_browser_t* pointer) => Slots[(nint)pointer].Identifier;
        [UnmanagedCallersOnly]
        private static cef_frame_t* GetMainFrame(cef_browser_t* pointer) => null;
        [UnmanagedCallersOnly]
        private static int IsSame(cef_browser_t* pointer, cef_browser_t* other)
        {
            var same = pointer == other;
            Interlocked.Decrement(ref Slots[(nint)other].References);
            return same ? 1 : 0;
        }
    }
}
