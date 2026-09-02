using System.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NUnit.Framework;
using Xilium.CefGlue.WPF.Platform;

namespace CefGlue.WPF.Tests
{
    [TestFixture]
    [Apartment(ApartmentState.STA)]
    public class WpfPopupTests
    {
        [Test]
        public void DisposePreventsQueuedOpenAndResizeOperations()
        {
            var popup = new Popup
            {
                Width = 1,
                Height = 2,
                HorizontalOffset = 3,
                VerticalOffset = 4
            };
            var host = new WpfPopup(popup);

            host.Open();
            host.MoveAndResize(10, 20, 30, 40);
            Assert.DoesNotThrow(host.Dispose);
            Assert.DoesNotThrow(host.Dispose);
            DrainDispatcher(popup.Dispatcher);

            Assert.IsFalse(popup.IsOpen);
            Assert.AreEqual(1, popup.Width);
            Assert.AreEqual(2, popup.Height);
            Assert.AreEqual(3, popup.HorizontalOffset);
            Assert.AreEqual(4, popup.VerticalOffset);
        }

        private static void DrainDispatcher(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new System.Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
