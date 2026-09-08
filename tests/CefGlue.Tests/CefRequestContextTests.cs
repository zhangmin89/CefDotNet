using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Interop;

namespace CefGlue.Tests
{
    [TestFixture]
    public unsafe class CefRequestContextTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void NativeCallKeepsManagedContextReference(bool throughPreferenceManager)
        {
            var native = new NativeContext { References = 1 };
            native.Context._base._size = (UIntPtr)sizeof(cef_request_context_t);
            native.Context._base._add_ref = (IntPtr)(delegate* unmanaged<cef_base_ref_counted_t*, void>)&AddReference;
            native.Context._base._release = (IntPtr)(delegate* unmanaged<cef_base_ref_counted_t*, int>)&ReleaseReference;

            using (var context = CefRequestContext.FromNative(&native.Context))
            {
                var argument = throughPreferenceManager ? (cef_request_context_t*)((CefPreferenceManager)context).ToNative() : context.ToNative();
                // CEF consumes the reference passed to the native call.
                var release = (delegate* unmanaged<cef_base_ref_counted_t*, int>)argument->_base._release;
                release(&argument->_base);

                Assert.AreEqual(1, native.References, "The managed context must retain its own reference after the native call.");
            }

            Assert.AreEqual(0, native.References, "Disposing the managed context must release the final reference.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeContext
        {
            public cef_request_context_t Context;
            public int References;
        }

        [UnmanagedCallersOnly]
        private static void AddReference(cef_base_ref_counted_t* self) => ((NativeContext*)self)->References++;

        [UnmanagedCallersOnly]
        private static int ReleaseReference(cef_base_ref_counted_t* self) => --((NativeContext*)self)->References == 0 ? 1 : 0;
    }
}
