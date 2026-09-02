using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Xilium.CefGlue;
using Xilium.CefGlue.Interop;

namespace CefGlue.Tests
{
    internal unsafe sealed class CefRunContextMenuCallbackHarness : IDisposable
    {
        private static readonly ConcurrentDictionary<nint, CefRunContextMenuCallbackHarness> Instances = new ConcurrentDictionary<nint, CefRunContextMenuCallbackHarness>();
        private cef_run_context_menu_callback_t* _native;
        private int _referenceCount = 1;

        public CefRunContextMenuCallbackHarness()
        {
            _native = (cef_run_context_menu_callback_t*)Marshal.AllocHGlobal(sizeof(cef_run_context_menu_callback_t));
            *_native = default;
            Instances[(nint)_native] = this;
            _native->_base._size = new UIntPtr((uint)sizeof(cef_run_context_menu_callback_t));
            _native->_base._add_ref = (IntPtr)(delegate* unmanaged<cef_run_context_menu_callback_t*, void>)&AddRef;
            _native->_base._release = (IntPtr)(delegate* unmanaged<cef_run_context_menu_callback_t*, int>)&Release;
            _native->_base._has_one_ref = (IntPtr)(delegate* unmanaged<cef_run_context_menu_callback_t*, int>)&HasOneRef;
            _native->_base._has_at_least_one_ref = (IntPtr)(delegate* unmanaged<cef_run_context_menu_callback_t*, int>)&HasAtLeastOneRef;
            _native->_cont = (IntPtr)(delegate* unmanaged<cef_run_context_menu_callback_t*, int, CefEventFlags, void>)&Continue;
            _native->_cancel = (IntPtr)(delegate* unmanaged<cef_run_context_menu_callback_t*, void>)&Cancel;
            Callback = CefRunContextMenuCallback.FromNative(_native);
        }

        public CefRunContextMenuCallback Callback { get; }

        public int ContinueCount { get; private set; }

        public int CancelCount { get; private set; }

        public int LastCommandId { get; private set; }

        public CefEventFlags LastEventFlags { get; private set; }

        public void Dispose()
        {
            if (_native == null)
            {
                return;
            }

            Callback.Dispose();
            Instances.TryRemove((nint)_native, out _);
            Marshal.FreeHGlobal((IntPtr)_native);
            _native = null;
        }

        [UnmanagedCallersOnly]
        private static void AddRef(cef_run_context_menu_callback_t* self)
        {
            Interlocked.Increment(ref Instances[(nint)self]._referenceCount);
        }

        [UnmanagedCallersOnly]
        private static int Release(cef_run_context_menu_callback_t* self)
        {
            return Interlocked.Decrement(ref Instances[(nint)self]._referenceCount) == 0 ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        private static int HasOneRef(cef_run_context_menu_callback_t* self)
        {
            return Volatile.Read(ref Instances[(nint)self]._referenceCount) == 1 ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        private static int HasAtLeastOneRef(cef_run_context_menu_callback_t* self)
        {
            return Volatile.Read(ref Instances[(nint)self]._referenceCount) >= 1 ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        private static void Continue(cef_run_context_menu_callback_t* self, int commandId, CefEventFlags eventFlags)
        {
            var instance = Instances[(nint)self];
            instance.ContinueCount++;
            instance.LastCommandId = commandId;
            instance.LastEventFlags = eventFlags;
        }

        [UnmanagedCallersOnly]
        private static void Cancel(cef_run_context_menu_callback_t* self)
        {
            Instances[(nint)self].CancelCount++;
        }
    }
}
