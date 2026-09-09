using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Helpers;
using Xilium.CefGlue.Common.Helpers.Logger;
using Xilium.CefGlue.Common.Platform;
using Xilium.CefGlue.Interop;

namespace CefGlue.Tests
{
    [TestFixture]
    public class CommonBrowserAdapterTests
    {
        [TestCase(false, "https://initial.example/")]
        [TestCase(false, null)]
        [TestCase(true, "https://initial.example/")]
        [TestCase(true, null)]
        public void AddressUsesInitialUrlWhenBrowserOrMainFrameIsMissing(bool hasBrowser, string? initialUrl)
        {
            using var native = hasBrowser ? new BrowserWithoutMainFrame() : null;
            using var adapter = new CommonBrowserAdapter(this, nameof(CommonBrowserAdapterTests), new TestControl(), new NullLogger());
            typeof(CommonBrowserAdapter).GetField("_initialUrl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adapter, initialUrl);
            typeof(CommonBrowserAdapter).GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adapter, native?.Browser);

            Assert.AreEqual(initialUrl, adapter.Address);
        }

        [Test]
        public void DisposeDetachesControlEvents()
        {
            var control = new TestControl();
            var adapter = new CommonBrowserAdapter(this, nameof(CommonBrowserAdapterTests), control, new NullLogger());

            Assert.AreEqual(1, control.GotFocusSubscriberCount);
            Assert.AreEqual(1, control.SizeChangedSubscriberCount);

            adapter.Dispose();

            Assert.AreEqual(0, control.GotFocusSubscriberCount);
            Assert.AreEqual(0, control.SizeChangedSubscriberCount);
        }

        [Test]
        public void CreateBrowserAfterDisposeDoesNotAccessControl()
        {
            var control = new TestControl();
            var adapter = new CommonBrowserAdapter(this, nameof(CommonBrowserAdapterTests), control, new NullLogger());

            adapter.Dispose();
            var created = adapter.CreateBrowser(1, 1);

            Assert.IsFalse(created);
            Assert.AreEqual(0, control.GetHostViewHandleCallCount);
        }

        [Test]
        public void ExceptionSubscriberCannotEscapeErrorBoundary()
        {
            var control = new TestControl();
            var adapter = new TestBrowserAdapter(control);
            adapter.UnhandledException += delegate { throw new InvalidOperationException("subscriber failure"); };

            Assert.DoesNotThrow(() => adapter.ReportException(new InvalidOperationException("original failure")));

            adapter.Dispose();
        }

        private unsafe sealed class BrowserWithoutMainFrame : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct NativeBrowser
            {
                public cef_browser_t Browser;
                public int References;
            }

            private NativeBrowser* _native;

            public BrowserWithoutMainFrame()
            {
                _native = (NativeBrowser*)Marshal.AllocHGlobal(sizeof(NativeBrowser));
                *_native = default;
                _native->References = 1;
                _native->Browser._base._size = new UIntPtr((uint)sizeof(cef_browser_t));
                _native->Browser._base._add_ref = (nint)(delegate* unmanaged<void*, void>)&AddRef;
                _native->Browser._base._release = (nint)(delegate* unmanaged<void*, int>)&Release;
                _native->Browser._base._has_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasOneRef;
                _native->Browser._base._has_at_least_one_ref = (nint)(delegate* unmanaged<void*, int>)&HasAnyRef;
                _native->Browser._get_main_frame = (nint)(delegate* unmanaged<cef_browser_t*, cef_frame_t*>)&GetMainFrame;
                Browser = CefBrowser.FromNative(&_native->Browser);
            }

            public CefBrowser Browser { get; }

            public void Dispose()
            {
                Browser.Dispose();
                Assert.AreEqual(0, _native->References, "The native browser reference must be released.");
                Marshal.FreeHGlobal((nint)_native);
                _native = null;
            }

            [UnmanagedCallersOnly]
            private static void AddRef(void* pointer) => Interlocked.Increment(ref ((NativeBrowser*)pointer)->References);
            [UnmanagedCallersOnly]
            private static int Release(void* pointer) => Interlocked.Decrement(ref ((NativeBrowser*)pointer)->References) == 0 ? 1 : 0;
            [UnmanagedCallersOnly]
            private static int HasOneRef(void* pointer) => Volatile.Read(ref ((NativeBrowser*)pointer)->References) == 1 ? 1 : 0;
            [UnmanagedCallersOnly]
            private static int HasAnyRef(void* pointer) => Volatile.Read(ref ((NativeBrowser*)pointer)->References) > 0 ? 1 : 0;
            [UnmanagedCallersOnly]
            private static cef_frame_t* GetMainFrame(cef_browser_t* pointer) => null;
        }

        private sealed class TestBrowserAdapter : CommonBrowserAdapter
        {
            public TestBrowserAdapter(IControl control) : base(typeof(TestBrowserAdapter), nameof(TestBrowserAdapter), control, new NullLogger())
            {
            }

            public void ReportException(Exception exception)
            {
                HandleException(nameof(ReportException), exception);
            }
        }

        private sealed class TestControl : IControl
        {
            public event Action GotFocus;

            public event Action<CefSize> SizeChanged;

            public int GotFocusSubscriberCount => GotFocus?.GetInvocationList().Length ?? 0;

            public int SizeChangedSubscriberCount => SizeChanged?.GetInvocationList().Length ?? 0;

            public int GetHostViewHandleCallCount { get; private set; }

            public IntPtr? GetHostViewHandle(int initialWidth, int initialHeight)
            {
                GetHostViewHandleCallCount++;
                return null;
            }

            public void OpenContextMenu(IEnumerable<MenuEntry> menuEntries, int x, int y, CefRunContextMenuCallback callback)
            {
            }

            public void CloseContextMenu()
            {
            }

            public void SetTooltip(string text)
            {
            }

            public void InitializeRender(IntPtr browserHandle)
            {
            }

            public void DestroyRender()
            {
            }

            public bool SetCursor(IntPtr cursorHandle, CefCursorType cursorType)
            {
                return false;
            }
        }
    }
}
