using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Shared.Helpers;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;
using Xilium.CefGlue.Interop;

namespace CefGlue.Tests;

// ABI-controlled context states exercise the real renderer message handler without simulating Chromium navigation.
[TestFixture, NonParallelizable]
public class RendererEvaluationReviewFixTests
{
    [TestCase("null-context")]
    [TestCase("enter-failed")]
    [TestCase("success")]
    [TestCase("script-error")]
    public void EveryEvaluationReturnsItsTaskIdAndPreservesNormalResults(string mode)
    {
        CefRuntime.Load();
        using var native = new NativeEvaluation(mode);
        var dispatcher = new MessageDispatcher();
        var renderer = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Xilium.CefGlue.BrowserProcess.dll"));
        var engine = renderer.GetType("Xilium.CefGlue.BrowserProcess.JavascriptExecution.JavascriptExecutionEngineRenderSide", throwOnError: true)!;
        Activator.CreateInstance(engine, dispatcher);
        using (CefObjectTracker.StartTracking())
        {
            var frame = native.WrapFrame();
            using var message = new Messages.JsEvaluationRequest { TaskId = 42, Script = "40 + 2", Url = "review://evaluation.js", Line = 7 }.ToCefProcessMessage();
            Assert.DoesNotThrow(() => dispatcher.DispatchMessage(null!, frame, CefProcessId.Browser, message));
            Assert.IsNull(native.CallbackError);
            Assert.AreEqual(1, native.Results.Count, "Each accepted request must produce exactly one result.");
            Assert.AreEqual(CefProcessId.Browser, native.Target);
            var result = native.Results.Single();
            Assert.AreEqual(42, result.TaskId);
            Assert.AreEqual(mode == "success", result.Success);
            if (mode == "success") Assert.AreEqual("42", result.ResultAsJson);
            else
            {
                Assert.IsNotEmpty(result.Exception);
                if (mode == "script-error") Assert.AreEqual("review syntax failure", result.Exception);
                if (mode == "enter-failed") StringAssert.Contains("Could not enter context", result.Exception);
            }
        }
        var entered = mode is "success" or "script-error";
        Assert.AreEqual(mode == "null-context" ? 0 : 1, native.EnterCalls);
        Assert.AreEqual(entered ? 1 : 0, native.EvalCalls);
        Assert.AreEqual(entered ? 1 : 0, native.ExitCalls);
        Assert.AreEqual(mode == "null-context" ? 0 : 1, native.ContextReleases);
        if (entered)
        {
            StringAssert.Contains("40 + 2", native.Script);
            Assert.AreEqual("review://evaluation.js", native.Url);
            Assert.AreEqual(7, native.Line);
            CollectionAssert.AreEqual(new[] { "exit", "release" }, native.ContextOrder);
        }
    }

    private unsafe sealed class NativeEvaluation : IDisposable
    {
        private sealed class Slot
        {
            public nint Address;
            public int References;
            public required NativeEvaluation Owner;
            public string Text = "";
        }

        private static readonly ConcurrentDictionary<nint, Slot> Slots = new();
        private readonly List<Slot> _slots = new();
        private readonly string _mode;
        private readonly Slot _frame;
        private readonly Slot _context;
        private readonly Slot _value;
        private readonly Slot _exception;
        public List<Messages.JsEvaluationResult> Results { get; } = new();
        public Exception? CallbackError;
        public CefProcessId Target;
        public int EnterCalls;
        public int EvalCalls;
        public int ExitCalls;
        public int ContextReleases;
        public List<string> ContextOrder { get; } = new();
        public string Script = "";
        public string Url = "";
        public int Line;

        public NativeEvaluation(string mode)
        {
            _mode = mode;
            _frame = Add<cef_frame_t>(1);
            _context = Add<cef_v8context_t>(0);
            _value = Add<cef_v8value_t>(0);
            _exception = Add<cef_v8exception_t>(0);
            _value.Text = "42";
            _exception.Text = "review syntax failure";
            var frame = (cef_frame_t*)_frame.Address;
            frame->_get_v8context = (nint)(delegate* unmanaged<cef_frame_t*, cef_v8context_t*>)&GetContext;
            frame->_get_identifier = (nint)(delegate* unmanaged<cef_frame_t*, long>)&GetIdentifier;
            frame->_send_process_message = (nint)(delegate* unmanaged<cef_frame_t*, CefProcessId, cef_process_message_t*, void>)&Send;
            var context = (cef_v8context_t*)_context.Address;
            context->_enter = (nint)(delegate* unmanaged<cef_v8context_t*, int>)&Enter;
            context->_exit = (nint)(delegate* unmanaged<cef_v8context_t*, int>)&Exit;
            context->_eval = (nint)(delegate* unmanaged<cef_v8context_t*, cef_string_t*, cef_string_t*, int, cef_v8value_t**, cef_v8exception_t**, int>)&Evaluate;
            ((cef_v8value_t*)_value.Address)->_get_string_value = (nint)(delegate* unmanaged<cef_v8value_t*, cef_string_userfree*>)&GetValue;
            ((cef_v8exception_t*)_exception.Address)->_get_message = (nint)(delegate* unmanaged<cef_v8exception_t*, cef_string_userfree*>)&GetError;
        }

        public CefFrame WrapFrame() => CefFrame.FromNative((cef_frame_t*)_frame.Address);

        private Slot Add<T>(int references) where T : unmanaged
        {
            var pointer = (T*)Marshal.AllocHGlobal(sizeof(T));
            *pointer = default;
            var slot = new Slot { Address = (nint)pointer, References = references, Owner = this };
            var counted = (cef_base_ref_counted_t*)pointer;
            counted->_size = (UIntPtr)(uint)sizeof(T);
            counted->_add_ref = (nint)(delegate* unmanaged<void*, void>)&AddRef;
            counted->_release = (nint)(delegate* unmanaged<void*, int>)&Release;
            counted->_has_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasOneRef;
            counted->_has_at_least_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasAnyRef;
            Slots[slot.Address] = slot;
            _slots.Add(slot);
            return slot;
        }

        public void Dispose()
        {
            foreach (var slot in _slots) Assert.AreEqual(0, slot.References, "A native wrapper still owns a test allocation.");
            foreach (var slot in _slots) { Slots.TryRemove(slot.Address, out _); Marshal.FreeHGlobal(slot.Address); }
        }

        [UnmanagedCallersOnly]
        private static void AddRef(void* pointer) => Interlocked.Increment(ref Slots[(nint)pointer].References);
        [UnmanagedCallersOnly]
        private static int Release(void* pointer)
        {
            var slot = Slots[(nint)pointer];
            if (slot == slot.Owner._context) { slot.Owner.ContextReleases++; slot.Owner.ContextOrder.Add("release"); }
            return Interlocked.Decrement(ref slot.References) == 0 ? 1 : 0;
        }
        [UnmanagedCallersOnly]
        private static int HasOneRef(void* pointer) => Volatile.Read(ref Slots[(nint)pointer].References) == 1 ? 1 : 0;
        [UnmanagedCallersOnly]
        private static int HasAnyRef(void* pointer) => Volatile.Read(ref Slots[(nint)pointer].References) > 0 ? 1 : 0;
        [UnmanagedCallersOnly]
        private static long GetIdentifier(cef_frame_t* pointer) => 42;
        [UnmanagedCallersOnly]
        private static cef_v8context_t* GetContext(cef_frame_t* pointer)
        {
            var owner = Slots[(nint)pointer].Owner;
            if (owner._mode == "null-context") return null;
            Interlocked.Increment(ref owner._context.References);
            return (cef_v8context_t*)owner._context.Address;
        }
        [UnmanagedCallersOnly]
        private static int Enter(cef_v8context_t* pointer)
        {
            var owner = Slots[(nint)pointer].Owner;
            owner.EnterCalls++;
            return owner._mode == "enter-failed" ? 0 : 1;
        }
        [UnmanagedCallersOnly]
        private static int Exit(cef_v8context_t* pointer)
        {
            var owner = Slots[(nint)pointer].Owner;
            owner.ExitCalls++;
            owner.ContextOrder.Add("exit");
            return 1;
        }
        [UnmanagedCallersOnly]
        private static int Evaluate(cef_v8context_t* pointer, cef_string_t* script, cef_string_t* url, int line, cef_v8value_t** value, cef_v8exception_t** error)
        {
            var owner = Slots[(nint)pointer].Owner;
            owner.EvalCalls++;
            owner.Script = cef_string_t.ToString(script);
            owner.Url = cef_string_t.ToString(url);
            owner.Line = line;
            if (owner._mode == "script-error")
            {
                Interlocked.Increment(ref owner._exception.References);
                *error = (cef_v8exception_t*)owner._exception.Address;
                return 0;
            }
            Interlocked.Increment(ref owner._value.References);
            *value = (cef_v8value_t*)owner._value.Address;
            return 1;
        }
        [UnmanagedCallersOnly]
        private static cef_string_userfree* GetValue(cef_v8value_t* pointer) => StringValue((nint)pointer);
        [UnmanagedCallersOnly]
        private static cef_string_userfree* GetError(cef_v8exception_t* pointer) => StringValue((nint)pointer);
        private static cef_string_userfree* StringValue(nint pointer)
        {
            var text = libcef.string_userfree_alloc();
            cef_string_t.Copy(Slots[pointer].Text, (cef_string_t*)text);
            return text;
        }
        [UnmanagedCallersOnly]
        private static void Send(cef_frame_t* pointer, CefProcessId target, cef_process_message_t* message)
        {
            var owner = Slots[(nint)pointer].Owner;
            try
            {
                using var managed = CefProcessMessage.FromNative(message);
                owner.Target = target;
                owner.Results.Add(Messages.JsEvaluationResult.FromCefMessage(managed));
            }
            catch (Exception exception) { owner.CallbackError = exception; }
        }
    }
}
