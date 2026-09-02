using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Helpers;
using Xilium.CefGlue.Common.Helpers.Logger;
using Xilium.CefGlue.Common.Platform;

namespace CefGlue.Tests
{
    [TestFixture]
    public class CommonOffscreenBrowserAdapterTests
    {
        [Test]
        public void DisposeDetachesAllEventsAndDisposesOwnedResources()
        {
            var control = new TestOffScreenControlHost();
            var popup = new TestPopupHost();
            var adapter = new TestCommonOffscreenBrowserAdapter(control, popup);
            adapter.InitializeBrowserViewForTest();

            Assert.Greater(control.EventSubscriberCount, 0);
            Assert.Greater(popup.EventSubscriberCount, 0);

            adapter.Dispose();

            Assert.AreEqual(0, control.EventSubscriberCount);
            Assert.AreEqual(0, popup.EventSubscriberCount);
            Assert.AreEqual(1, popup.DisposeCount);
            Assert.AreEqual(1, control.TestRenderSurface.DisposeCount);
            Assert.AreEqual(1, popup.TestRenderSurface.DisposeCount);
        }

        [Test]
        public void PopupCallbacksAreIgnoredAfterDispose()
        {
            var control = new TestOffScreenControlHost();
            var popup = new TestPopupHost();
            var adapter = new TestCommonOffscreenBrowserAdapter(control, popup);
            var browserHost = (IOffscreenCefBrowserHost)adapter;

            browserHost.HandlePopupShow(true);
            browserHost.HandlePopupSizeChange(new CefRectangle(1, 2, 3, 4));
            Assert.AreEqual(1, popup.OpenCount);
            Assert.AreEqual(1, popup.MoveAndResizeCount);

            adapter.Dispose();
            browserHost.HandlePopupShow(false);
            browserHost.HandlePopupSizeChange(new CefRectangle(5, 6, 7, 8));

            Assert.AreEqual(0, popup.CloseCount);
            Assert.AreEqual(1, popup.MoveAndResizeCount);
        }

        private sealed class TestCommonOffscreenBrowserAdapter : CommonOffscreenBrowserAdapter
        {
            public TestCommonOffscreenBrowserAdapter(TestOffScreenControlHost control, TestPopupHost popup) : base(typeof(TestCommonOffscreenBrowserAdapter), nameof(TestCommonOffscreenBrowserAdapter), control, popup, new NullLogger())
            {
            }

            public void InitializeBrowserViewForTest()
            {
                var windowInfo = CefWindowInfo.Create();
                try
                {
                    SetupBrowserView(windowInfo, 1, 1, IntPtr.Zero);
                }
                finally
                {
                    windowInfo.Dispose();
                }
            }
        }

        private class TestOffScreenControlHost : IOffScreenControlHost
        {
            public event Action GotFocus;
            public event Action<CefSize> SizeChanged;
            public event Action LostFocus;
            public event KeyEventHandler KeyDown;
            public event KeyEventHandler KeyUp;
            public event TextInputEventHandler TextInput;
            public event Action<IOffScreenControlHost, CefMouseEvent, CefMouseButtonType, int> MouseButtonPressed;
            public event Action<CefMouseEvent, CefMouseButtonType> MouseButtonReleased;
            public event Action<CefMouseEvent> MouseLeave;
            public event Action<CefMouseEvent> MouseMoved;
            public event Action<CefMouseEvent, int, int> MouseWheelChanged;
            public event Action<CefMouseEvent, CefDragData, CefDragOperationsMask> DragEnter;
            public event Action<CefMouseEvent, CefDragOperationsMask> DragOver;
            public event Action DragLeave;
            public event Action<CefMouseEvent, CefDragOperationsMask> Drop;
            public event Action<float> ScreenInfoChanged;
            public event Action<bool> VisibilityChanged;

            public TestRenderSurface TestRenderSurface { get; } = new TestRenderSurface();

            public OffScreenRenderSurface RenderSurface => TestRenderSurface;

            public int EventSubscriberCount => GetSubscriberCount(GotFocus) + GetSubscriberCount(SizeChanged) + GetSubscriberCount(LostFocus) + GetSubscriberCount(KeyDown) + GetSubscriberCount(KeyUp) + GetSubscriberCount(TextInput) + GetSubscriberCount(MouseButtonPressed) + GetSubscriberCount(MouseButtonReleased) + GetSubscriberCount(MouseLeave) + GetSubscriberCount(MouseMoved) + GetSubscriberCount(MouseWheelChanged) + GetSubscriberCount(DragEnter) + GetSubscriberCount(DragOver) + GetSubscriberCount(DragLeave) + GetSubscriberCount(Drop) + GetSubscriberCount(ScreenInfoChanged) + GetSubscriberCount(VisibilityChanged);

            public IntPtr? GetHostViewHandle(int initialWidth, int initialHeight)
            {
                return IntPtr.Zero;
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

            public void Focus()
            {
            }

            public CefPoint PointToScreen(CefPoint point, float deviceScaleFactor)
            {
                return point;
            }

            public Task<CefDragOperationsMask> StartDrag(CefDragData dragData, CefDragOperationsMask allowedOps, int x, int y)
            {
                return Task.FromResult(CefDragOperationsMask.None);
            }

            public void UpdateDragCursor(CefDragOperationsMask allowedOps)
            {
            }

            private static int GetSubscriberCount(Delegate handler)
            {
                return handler?.GetInvocationList().Length ?? 0;
            }
        }

        private sealed class TestPopupHost : TestOffScreenControlHost, IOffScreenPopupHost
        {
            public int Width => TestRenderSurface.Width;
            public int Height => TestRenderSurface.Height;
            public int OffsetX { get; private set; }
            public int OffsetY { get; private set; }
            public int OpenCount { get; private set; }
            public int CloseCount { get; private set; }
            public int MoveAndResizeCount { get; private set; }
            public int DisposeCount { get; private set; }

            public void MoveAndResize(int x, int y, int width, int height)
            {
                OffsetX = x;
                OffsetY = y;
                TestRenderSurface.Resize(width, height);
                MoveAndResizeCount++;
            }

            public void Open()
            {
                OpenCount++;
            }

            public void Close()
            {
                CloseCount++;
            }

            public void Dispose()
            {
                DisposeCount++;
            }
        }

        private sealed class TestRenderSurface : OffScreenRenderSurface
        {
            public int DisposeCount { get; private set; }

            public override bool AllowsTransparency => false;
            protected override int BytesPerPixel => 4;
            protected override int RenderedWidth => 0;
            protected override int RenderedHeight => 0;

            public override void Dispose()
            {
                DisposeCount++;
                base.Dispose();
            }

            protected override void CreateBitmap(int width, int height)
            {
            }

            protected override Task ExecuteInUIThread(Action action)
            {
                action();
                return Task.CompletedTask;
            }

            protected override void UpdateBitmap(IntPtr sourceBuffer, int sourceBufferSize, int stride, CefRectangle updateRegion)
            {
            }

            protected override Action BeginBitmapUpdate()
            {
                return () => { };
            }
        }
    }
}
