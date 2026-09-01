using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Helpers;

namespace CefGlue.Tests
{
    [TestFixture]
    public class OffScreenRenderSurfaceTests
    {
        [Test]
        public void RenderAndResizeCannotReviveDisposedSurface()
        {
            var surface = new TestRenderSurface();
            surface.Resize(1, 1);
            surface.Dispose();

            surface.Resize(2, 2);
            var renderTask = surface.Render(IntPtr.Zero, 1, 1, Array.Empty<CefRectangle>());

            Assert.AreEqual(1, surface.Width);
            Assert.AreEqual(1, surface.Height);
            Assert.AreEqual(0, surface.UiDispatchCount);
            Assert.IsTrue(renderTask.IsCompletedSuccessfully);
        }

        private sealed class TestRenderSurface : OffScreenRenderSurface
        {
            public int UiDispatchCount { get; private set; }

            public override bool AllowsTransparency => false;

            protected override int BytesPerPixel => 4;

            protected override int RenderedWidth => 0;

            protected override int RenderedHeight => 0;

            protected override void CreateBitmap(int width, int height)
            {
            }

            protected override Task ExecuteInUIThread(Action action)
            {
                UiDispatchCount++;
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
