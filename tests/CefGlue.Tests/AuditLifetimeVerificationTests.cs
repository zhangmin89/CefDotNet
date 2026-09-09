using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Interop;

namespace CefGlue.Tests;

// Native ABI stubs count reference releases. They do not simulate Chromium's V8 destructor.
[TestFixture, NonParallelizable, Category("AuditLifetimeVerification")]
[Explicit("Manual native-reference probe; select this fixture explicitly.")]
public class AuditLifetimeVerificationTests
{
    private static Assembly Renderer => Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Xilium.CefGlue.BrowserProcess.dll"));

    [Test]
    public void V11_UntrackedContextIsEventuallyReleasedByFinalizer()
    {
        using var native = new NativeReferences();
        var contextSlot = native.AddContext();
        var reference = LeaveUntrackedContext(native, contextSlot);
        TestContext.Out.WriteLine($"V11 immediately after failure-style scope exit: Exit calls={contextSlot.ExitCalls}; Release calls={contextSlot.ReleaseCalls}");
        Assert.AreEqual(1, contextSlot.ExitCalls);
        Assert.AreEqual(0, contextSlot.ReleaseCalls);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        TestContext.Out.WriteLine($"V11 after GC: wrapperAlive={reference.IsAlive}; Release calls={contextSlot.ReleaseCalls}; references={contextSlot.References}");
        Assert.IsFalse(reference.IsAlive);
        Assert.AreEqual(1, contextSlot.ReleaseCalls);
        Assert.AreEqual(0, contextSlot.References);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LeaveUntrackedContext(NativeReferences native, NativeReferences.Slot slot)
    {
        using var tracking = CefObjectTracker.StartTracking();
        var context = native.WrapContext(slot);
        var type = Renderer.GetType("Xilium.CefGlue.BrowserProcess.ContextWrapper", true)!;
        using var wrapper = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { context, false }, null)!;
        return new WeakReference(context);
    }

    [Test]
    public void V10_PostTaskFalseReleasesManagedHandlesExactlyOnce()
    {
        using var native = new NativeReferences();
        var runner = native.AddRunner();
        var contextSlot = native.AddContext(runner.Address);
        var context = native.WrapContext(contextSlot);
        var promise = native.AddValue();
        var resolve = native.AddValue();
        var reject = native.AddValue();
        var holderType = Renderer.GetType("Xilium.CefGlue.BrowserProcess.ObjectBinding.PromiseHolder", true)!;
        var holder = Activator.CreateInstance(holderType, native.WrapValue(promise), native.WrapValue(resolve), native.WrapValue(reject), context)!;
        var dispatcherType = Renderer.GetType("Xilium.CefGlue.BrowserProcess.ObjectBinding.JavascriptToNativeDispatcherRenderSide", true)!;
        var dispatcher = RuntimeHelpers.GetUninitializedObject(dispatcherType);
        var promiseField = dispatcherType.GetField("_pendingBoundPromises", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var promises = (IDictionary)Activator.CreateInstance(promiseField.FieldType)!;
        promises.Add(holder, (byte)0);
        promiseField.SetValue(dispatcher, promises);
        dispatcherType.GetField("_pendingBoundPromiseSyncRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dispatcher, new object());
        var schedule = dispatcherType.GetMethod("SchedulePendingBoundPromiseCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Exception? failure = null;
        var workerId = 0;
        var worker = new Thread(() =>
        {
            workerId = Environment.CurrentManagedThreadId;
            try { schedule.Invoke(dispatcher, new object[] { holder, Task.FromResult(true) }); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        worker.Start();
        Assert.IsTrue(worker.Join(10000), "Reference-release probe did not finish.");
        TestContext.Out.WriteLine($"V10 injected PostTask=false; Post calls={runner.PostCalls}; pending holders={promises.Count}; worker={workerId}; error={failure}");
        Assert.IsNull(failure);
        Assert.AreEqual(1, runner.PostCalls);
        Assert.AreEqual(0, promises.Count);
        foreach (var slot in new[] { contextSlot, promise, resolve, reject })
        {
            TestContext.Out.WriteLine($"V10 {slot.Kind}: Release calls={slot.ReleaseCalls}; references={slot.References}; release thread={slot.ReleaseThread}");
            Assert.AreEqual(1, slot.ReleaseCalls);
            Assert.AreEqual(0, slot.References);
            Assert.AreEqual(workerId, slot.ReleaseThread);
        }
        TestContext.Out.WriteLine("V10 limitation: this verifies managed cleanup and native reference-release calls, not Chromium V8 destruction after renderer-thread shutdown.");
    }

    private unsafe sealed class NativeReferences : IDisposable
    {
        internal sealed class Slot
        {
            public nint Address;
            public string Kind = "";
            public int References = 1;
            public int ReleaseCalls;
            public int ReleaseThread;
            public int ExitCalls;
            public int PostCalls;
            public nint Runner;
        }

        private static readonly ConcurrentDictionary<nint, Slot> Slots = new();
        private readonly List<Slot> owned = new();
        private readonly List<WeakReference> wrappers = new();

        private Slot Add<T>(string kind) where T : unmanaged
        {
            var pointer = (T*)Marshal.AllocHGlobal(sizeof(T));
            *pointer = default;
            var slot = new Slot { Address = (nint)pointer, Kind = kind };
            var counted = (cef_base_ref_counted_t*)pointer;
            counted->_size = (UIntPtr)(uint)sizeof(T);
            counted->_add_ref = (nint)(delegate* unmanaged<void*, void>)&AddRef;
            counted->_release = (nint)(delegate* unmanaged<void*, int>)&Release;
            counted->_has_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasOneRef;
            counted->_has_at_least_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasAnyRef;
            Slots[slot.Address] = slot;
            owned.Add(slot);
            return slot;
        }

        public Slot AddContext(nint runner = 0)
        {
            var slot = Add<cef_v8context_t>("context");
            slot.Runner = runner;
            var pointer = (cef_v8context_t*)slot.Address;
            pointer->_get_browser = (nint)(delegate* unmanaged<cef_v8context_t*, cef_browser_t*>)&GetBrowser;
            pointer->_get_task_runner = (nint)(delegate* unmanaged<cef_v8context_t*, cef_task_runner_t*>)&GetRunner;
            pointer->_exit = (nint)(delegate* unmanaged<cef_v8context_t*, int>)&Exit;
            return slot;
        }

        public Slot AddRunner()
        {
            var slot = Add<cef_task_runner_t>("runner");
            ((cef_task_runner_t*)slot.Address)->_post_task = (nint)(delegate* unmanaged<cef_task_runner_t*, cef_task_t*, int>)&PostTask;
            return slot;
        }

        public Slot AddValue() => Add<cef_v8value_t>("value");

        public CefV8Context WrapContext(Slot slot)
        {
            var value = CefV8Context.FromNative((cef_v8context_t*)slot.Address);
            wrappers.Add(new WeakReference(value, true));
            return value;
        }

        public CefV8Value WrapValue(Slot slot)
        {
            var value = CefV8Value.FromNative((cef_v8value_t*)slot.Address);
            wrappers.Add(new WeakReference(value, true));
            return value;
        }

        public void Dispose()
        {
            foreach (var reference in wrappers) (reference.Target as IDisposable)?.Dispose();
            GC.WaitForPendingFinalizers();
            foreach (var slot in owned) { Slots.TryRemove(slot.Address, out _); Marshal.FreeHGlobal(slot.Address); }
        }

        [UnmanagedCallersOnly]
        private static void AddRef(void* pointer) => Interlocked.Increment(ref Slots[(nint)pointer].References);
        [UnmanagedCallersOnly]
        private static int Release(void* pointer)
        {
            var slot = Slots[(nint)pointer];
            Interlocked.Increment(ref slot.ReleaseCalls);
            slot.ReleaseThread = Environment.CurrentManagedThreadId;
            return Interlocked.Decrement(ref slot.References) == 0 ? 1 : 0;
        }
        [UnmanagedCallersOnly]
        private static int HasOneRef(void* pointer) => Volatile.Read(ref Slots[(nint)pointer].References) == 1 ? 1 : 0;
        [UnmanagedCallersOnly]
        private static int HasAnyRef(void* pointer) => Volatile.Read(ref Slots[(nint)pointer].References) > 0 ? 1 : 0;
        [UnmanagedCallersOnly]
        private static cef_browser_t* GetBrowser(cef_v8context_t* pointer) => null;
        [UnmanagedCallersOnly]
        private static cef_task_runner_t* GetRunner(cef_v8context_t* pointer)
        {
            var runner = Slots[(nint)pointer].Runner;
            Interlocked.Increment(ref Slots[runner].References);
            return (cef_task_runner_t*)runner;
        }
        [UnmanagedCallersOnly]
        private static int Exit(cef_v8context_t* pointer) { Interlocked.Increment(ref Slots[(nint)pointer].ExitCalls); return 1; }
        [UnmanagedCallersOnly]
        private static int PostTask(cef_task_runner_t* pointer, cef_task_t* task)
        {
            Interlocked.Increment(ref Slots[(nint)pointer].PostCalls);
            // Match ownership of the reference transferred by CefTask.ToNative().
            var release = (delegate* unmanaged<cef_task_t*, int>)task->_base._release;
            release(task);
            return 0;
        }
    }
}
