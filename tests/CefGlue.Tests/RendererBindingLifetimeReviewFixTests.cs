using System.Collections;
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

// Count native ownership in the real call handler; this does not simulate Chromium's V8 destructor.
[TestFixture, NonParallelizable]
public class RendererBindingLifetimeReviewFixTests
{
    [TestCase("no-frame")]
    [TestCase("enter-failed")]
    [TestCase("factory-failed")]
    [TestCase("success")]
    public void ContextIsReleasedAfterExitUnlessThePendingPromiseOwnsIt(string mode)
    {
        CefRuntime.Load();
        using var native = new NativeBinding(mode);
        var renderer = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Xilium.CefGlue.BrowserProcess.dll"));
        var type = renderer.GetType("Xilium.CefGlue.BrowserProcess.ObjectBinding.JavascriptToNativeDispatcherRenderSide", true)!;
        // Extension registration requires a renderer process. Initialize only the dispatcher state here.
        var dispatcher = RuntimeHelpers.GetUninitializedObject(type);
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            field.SetValue(dispatcher, Activator.CreateInstance(field.FieldType));
        var call = type.GetMethod("HandleNativeObjectCall", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Messages.NativeObjectCallRequest), typeof(CefV8Context) }, null)!;
        var released = type.GetMethod("HandleContextReleased")!;
        var pending = (IDictionary)type.GetField("_pendingCalls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dispatcher)!;
        using (CefObjectTracker.StartTracking())
        {
            var context = native.WrapContext();
            try
            {
                var request = new Messages.NativeObjectCallRequest { ObjectName = "review", MemberName = "Answer", ArgumentsAsJson = "[]" };
                object? result = null;
                var failure = Assert.Catch(() =>
                {
                    try { result = call.Invoke(dispatcher, new object[] { request, context }); }
                    catch (TargetInvocationException exception) { throw exception.InnerException!; }
                    if (mode is "no-frame" or "success") throw new SuccessfulInvocation();
                });
                if (mode == "enter-failed") Assert.IsInstanceOf<InvalidOperationException>(failure);
                else if (mode == "factory-failed") Assert.IsInstanceOf<NullReferenceException>(failure);
                else Assert.IsInstanceOf<SuccessfulInvocation>(failure);
                Assert.IsNull(native.CallbackError);
                Assert.AreEqual(mode == "enter-failed" ? 0 : 1, native.ExitCalls);
                Assert.AreEqual(mode == "success" ? 1 : 0, pending.Count);
                if (mode == "success")
                {
                    Assert.IsNotNull(result);
                    Assert.AreEqual(1, native.ContextReferences, "The accepted promise must retain its context.");
                    Assert.AreEqual(1, native.Sent.Count);
                    Assert.AreEqual("review", native.Sent[0].ObjectName);
                    Assert.AreEqual("Answer", native.Sent[0].MemberName);
                    Assert.AreEqual("[]", native.Sent[0].ArgumentsAsJson);
                    Assert.IsTrue(pending.Contains(native.Sent[0].CallId), "The sent ID must identify the pending call, including ID zero.");
                    released.Invoke(dispatcher, new object[] { context });
                }
                else Assert.IsNull(result);
                Assert.AreEqual(0, native.ContextReferences, "Failed calls must release before returning, without GC or tracker cleanup.");
                CollectionAssert.AreEqual(mode == "enter-failed" ? new[] { "release" } : new[] { "exit", "release" }, native.ContextOrder);
                Assert.AreEqual(0, pending.Count);
            }
            finally
            {
                if (pending.Count != 0) released.Invoke(dispatcher, new object[] { context });
                context.Dispose();
            }
        }
    }

    private sealed class SuccessfulInvocation : Exception { }

    private unsafe sealed class NativeBinding : IDisposable
    {
        private sealed class Slot
        {
            public nint Address;
            public int References;
            public required NativeBinding Owner;
            public string Kind = "";
        }

        private static readonly ConcurrentDictionary<nint, Slot> Slots = new();
        private readonly Dictionary<string, Slot> _owned = new();
        private readonly string _mode;
        public Exception? CallbackError;
        public int ExitCalls;
        public List<string> ContextOrder { get; } = new();
        public List<Messages.NativeObjectCallRequest> Sent { get; } = new();
        public int ContextReferences => _owned["context"].References;

        public NativeBinding(string mode)
        {
            _mode = mode;
            var context = (cef_v8context_t*)Add<cef_v8context_t>("context", 1).Address;
            context->_enter = (nint)(delegate* unmanaged<cef_v8context_t*, int>)&Enter;
            context->_exit = (nint)(delegate* unmanaged<cef_v8context_t*, int>)&Exit;
            context->_get_frame = (nint)(delegate* unmanaged<cef_v8context_t*, cef_frame_t*>)&GetFrame;
            context->_get_browser = (nint)(delegate* unmanaged<cef_v8context_t*, cef_browser_t*>)&GetBrowser;
            context->_get_global = (nint)(delegate* unmanaged<cef_v8context_t*, cef_v8value_t*>)&GetGlobal;
            context->_is_same = (nint)(delegate* unmanaged<cef_v8context_t*, cef_v8context_t*, int>)&IsSame;
            var frame = (cef_frame_t*)Add<cef_frame_t>("frame").Address;
            frame->_send_process_message = (nint)(delegate* unmanaged<cef_frame_t*, CefProcessId, cef_process_message_t*, void>)&Send;
            Add<cef_browser_t>("browser");
            foreach (var kind in new[] { "global", "cefglue", "createPromise", "data", "promise", "resolve", "reject" })
            {
                var value = (cef_v8value_t*)Add<cef_v8value_t>(kind).Address;
                value->_get_value_bykey = (nint)(delegate* unmanaged<cef_v8value_t*, cef_string_t*, cef_v8value_t*>)&GetValue;
                value->_execute_function_with_context = (nint)(delegate* unmanaged<cef_v8value_t*, cef_v8context_t*, cef_v8value_t*, UIntPtr, cef_v8value_t**, cef_v8value_t*>)&Execute;
            }
        }

        public CefV8Context WrapContext() => CefV8Context.FromNative((cef_v8context_t*)_owned["context"].Address);

        private Slot Add<T>(string kind, int references = 0) where T : unmanaged
        {
            var pointer = (T*)Marshal.AllocHGlobal(sizeof(T));
            *pointer = default;
            var slot = new Slot { Address = (nint)pointer, References = references, Owner = this, Kind = kind };
            var counted = (cef_base_ref_counted_t*)pointer;
            counted->_size = (UIntPtr)(uint)sizeof(T);
            counted->_add_ref = (nint)(delegate* unmanaged<void*, void>)&AddRef;
            counted->_release = (nint)(delegate* unmanaged<void*, int>)&Release;
            counted->_has_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasOneRef;
            counted->_has_at_least_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasAnyRef;
            Slots[slot.Address] = slot;
            _owned.Add(kind, slot);
            return slot;
        }

        public void Dispose()
        {
            foreach (var slot in _owned.Values) Assert.AreEqual(0, slot.References, slot.Kind + " still has native owners.");
            foreach (var slot in _owned.Values) { Slots.TryRemove(slot.Address, out _); Marshal.FreeHGlobal(slot.Address); }
        }

        private static void* Acquire(NativeBinding owner, string kind)
        {
            var slot = owner._owned[kind];
            Interlocked.Increment(ref slot.References);
            return (void*)slot.Address;
        }

        private static int ReleaseReference(void* pointer)
        {
            var slot = Slots[(nint)pointer];
            var remaining = Interlocked.Decrement(ref slot.References);
            if (remaining == 0 && slot.Kind == "context") slot.Owner.ContextOrder.Add("release");
            return remaining == 0 ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        private static void AddRef(void* pointer) => Interlocked.Increment(ref Slots[(nint)pointer].References);
        [UnmanagedCallersOnly]
        private static int Release(void* pointer) => ReleaseReference(pointer);
        [UnmanagedCallersOnly]
        private static int HasOneRef(void* pointer) => Volatile.Read(ref Slots[(nint)pointer].References) == 1 ? 1 : 0;
        [UnmanagedCallersOnly]
        private static int HasAnyRef(void* pointer) => Volatile.Read(ref Slots[(nint)pointer].References) > 0 ? 1 : 0;
        [UnmanagedCallersOnly]
        private static int Enter(cef_v8context_t* pointer) => Slots[(nint)pointer].Owner._mode == "enter-failed" ? 0 : 1;
        [UnmanagedCallersOnly]
        private static int Exit(cef_v8context_t* pointer)
        {
            var owner = Slots[(nint)pointer].Owner;
            owner.ExitCalls++;
            owner.ContextOrder.Add("exit");
            return 1;
        }
        [UnmanagedCallersOnly]
        private static int IsSame(cef_v8context_t* pointer, cef_v8context_t* other)
        {
            var same = pointer == other;
            ReleaseReference(other);
            return same ? 1 : 0;
        }
        [UnmanagedCallersOnly]
        private static cef_frame_t* GetFrame(cef_v8context_t* pointer)
        {
            var owner = Slots[(nint)pointer].Owner;
            return owner._mode == "no-frame" ? null : (cef_frame_t*)Acquire(owner, "frame");
        }
        [UnmanagedCallersOnly]
        private static cef_browser_t* GetBrowser(cef_v8context_t* pointer) => (cef_browser_t*)Acquire(Slots[(nint)pointer].Owner, "browser");
        [UnmanagedCallersOnly]
        private static cef_v8value_t* GetGlobal(cef_v8context_t* pointer) => (cef_v8value_t*)Acquire(Slots[(nint)pointer].Owner, "global");
        [UnmanagedCallersOnly]
        private static cef_v8value_t* GetValue(cef_v8value_t* pointer, cef_string_t* key)
        {
            var owner = Slots[(nint)pointer].Owner;
            try { return (cef_v8value_t*)Acquire(owner, cef_string_t.ToString(key)); }
            catch (Exception exception) { owner.CallbackError = exception; return null; }
        }
        [UnmanagedCallersOnly]
        private static cef_v8value_t* Execute(cef_v8value_t* pointer, cef_v8context_t* context, cef_v8value_t* obj, UIntPtr count, cef_v8value_t** arguments)
        {
            var owner = Slots[(nint)pointer].Owner;
            ReleaseReference(context);
            return owner._mode == "factory-failed" ? null : (cef_v8value_t*)Acquire(owner, "data");
        }
        [UnmanagedCallersOnly]
        private static void Send(cef_frame_t* pointer, CefProcessId target, cef_process_message_t* message)
        {
            var owner = Slots[(nint)pointer].Owner;
            try
            {
                using var managed = CefProcessMessage.FromNative(message);
                owner.Sent.Add(Messages.NativeObjectCallRequest.FromCefMessage(managed));
            }
            catch (Exception exception) { owner.CallbackError = exception; }
        }
    }
}
