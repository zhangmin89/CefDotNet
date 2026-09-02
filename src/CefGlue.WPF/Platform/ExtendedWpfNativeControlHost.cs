using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Xilium.CefGlue.WPF.Platform
{
    internal class ExtendedWpfNativeControlHost : HwndHost
    {
        [DllImport("user32.dll", EntryPoint = "DestroyWindow", CharSet = CharSet.Unicode)]
        private static extern bool DestroyNativeWindow(IntPtr hwnd);

        private readonly IntPtr _browserHandle;

        public ExtendedWpfNativeControlHost(IntPtr browserHandle)
        {
            _browserHandle = browserHandle;
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            return new HandleRef(this, _browserHandle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            // nothing to do
        }

        public static void DestroyWindow(IntPtr browserHandle)
        {
            DestroyNativeWindow(browserHandle);
        }
    }
}
