using System;
using System.Collections.Generic;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Helpers;
using Xilium.CefGlue.Common.Helpers.Logger;
using Xilium.CefGlue.Common.Platform;

namespace CefGlue.Tests
{
    [TestFixture]
    public class CommonBrowserAdapterTests
    {
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
