using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.WPF;
using Xilium.CefGlue.WPF.Platform;

namespace CefGlue.WPF.Tests
{
    [TestFixture]
    [Apartment(ApartmentState.STA)]
    public class WpfDisplayLifecycleReviewTests
    {
        [Test]
        public void CloseReleasesNativePopupWindowAndDisposePreventsReopening()
        {
            var target = new Border { Width = 16, Height = 16 };
            var owner = new Window { Content = target, Width = 32, Height = 32, ShowInTaskbar = false };
            var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Relative, Width = 16, Height = 16 };
            var host = new WpfPopup(popup);
            try
            {
                owner.Show();
                host.Open();
                DrainDispatcher(popup.Dispatcher);
                var firstSource = (HwndSource)PresentationSource.FromVisual(popup.Child);
                Assert.IsNotNull(firstSource);
                Assert.IsFalse(firstSource.IsDisposed);
                Assert.AreNotEqual(IntPtr.Zero, firstSource.Handle);

                host.Close();
                DrainDispatcher(popup.Dispatcher);
                Assert.IsFalse(popup.IsOpen);
                Assert.IsTrue(firstSource.IsDisposed, "Closing the WPF Popup must release its native window.");

                host.Open();
                DrainDispatcher(popup.Dispatcher);
                var secondSource = (HwndSource)PresentationSource.FromVisual(popup.Child);
                Assert.IsNotNull(secondSource);
                Assert.AreNotSame(firstSource, secondSource);
                Assert.IsFalse(secondSource.IsDisposed);

                host.Dispose();
                DrainDispatcher(popup.Dispatcher);
                Assert.IsFalse(popup.IsOpen);
                Assert.IsTrue(secondSource.IsDisposed);
                host.Open();
                Assert.DoesNotThrow(host.Dispose);
                DrainDispatcher(popup.Dispatcher);
                Assert.IsFalse(popup.IsOpen);
                TestContext.Out.WriteLine("Close disposed the first HwndSource; reopen created another; Dispose disposed the second and prevented reopening.");
            }
            finally
            {
                host.Dispose();
                host.RenderSurface.Dispose();
                popup.IsOpen = false;
                owner.Close();
                DrainDispatcher(popup.Dispatcher);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SurfaceDisposeDoesNotBreakImageRendering(bool workerThread)
        {
            var image = new Image { Width = 8, Height = 8 };
            var surface = new WpfRenderSurface(image);
            var pixels = new byte[8 * 8 * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
            var buffer = Marshal.AllocHGlobal(pixels.Length);
            try
            {
                Marshal.Copy(pixels, 0, buffer, pixels.Length);
                surface.Resize(8, 8);
                var pendingRender = surface.Render(buffer, 8, 8, new[] { new CefRectangle(0, 0, 8, 8) });
                DrainDispatcher(image.Dispatcher);
                pendingRender.GetAwaiter().GetResult();
                Assert.IsInstanceOf<WriteableBitmap>(image.Source);
                var bitmap = image.Source;

                if (workerThread) Task.Run(surface.Dispose).GetAwaiter().GetResult();
                else surface.Dispose();
                DrainDispatcher(image.Dispatcher);

                image.Measure(new Size(8, 8));
                image.Arrange(new Rect(0, 0, 8, 8));
                var render = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
                Assert.DoesNotThrow(() => render.Render(image));
                Assert.DoesNotThrow(surface.Dispose);
                TestContext.Out.WriteLine($"Worker-thread Dispose={workerThread}; Image.Source retains bitmap={ReferenceEquals(bitmap, image.Source)}; bitmap implements IDisposable={typeof(IDisposable).IsAssignableFrom(bitmap.GetType())}; RenderTargetBitmap.Render completed.");
            }
            finally
            {
                surface.Dispose();
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void DrainDispatcher(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
