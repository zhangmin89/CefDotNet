using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Interop;

namespace CefGlue.Tests
{
    [TestFixture]
    public unsafe class CefV8HandlerTests
    {
        [Test]
        public void ManagedExceptionDoesNotEscapeNativeExecuteCallback()
        {
            var handler = new ThrowingV8Handler();
            var nativeHandler = handler.ToNative();
            var releaseValue = new cef_base_ref_counted_t.release_delegate(_ => 1);
            var nativeValue = new cef_v8value_t();
            nativeValue._base._release = Marshal.GetFunctionPointerForDelegate(releaseValue);
            var execute = Marshal.GetDelegateForFunctionPointer<cef_v8handler_t.execute_delegate>(nativeHandler->_execute);
            cef_v8value_t* returnValue = null;

            var handled = execute(nativeHandler, null, &nativeValue, UIntPtr.Zero, null, &returnValue, null);

            Assert.AreEqual(1, handled);
            Assert.AreEqual(1, handler.InvocationCount);
            Assert.IsTrue(returnValue == null);

            var releaseHandler = Marshal.GetDelegateForFunctionPointer<cef_v8handler_t.release_delegate>(nativeHandler->_base._release);
            releaseHandler(nativeHandler);
            GC.KeepAlive(releaseValue);
        }

        private sealed class ThrowingV8Handler : CefV8Handler
        {
            public int InvocationCount { get; private set; }

            protected override bool Execute(string name, CefV8Value obj, CefV8Value[] arguments, out CefV8Value returnValue, out string exception)
            {
                InvocationCount++;
                throw new InvalidOperationException("callback failure");
            }
        }
    }
}
