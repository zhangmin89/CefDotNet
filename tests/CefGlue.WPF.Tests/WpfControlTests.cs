using System;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using NUnit.Framework;
using Xilium.CefGlue.WPF.Platform;

namespace CefGlue.WPF.Tests
{
    [TestFixture]
    [Apartment(ApartmentState.STA)]
    public class WpfControlTests
    {
        [Test]
        public void DestroyRenderBeforeQueuedInitializationDoesNotAttachDestroyedHost()
        {
            var contentControl = new ContentControl();
            var control = new WpfControl(contentControl);

            control.InitializeRender(new IntPtr(1));

            Assert.DoesNotThrow(control.DestroyRender);
            DrainDispatcher(contentControl.Dispatcher);
            Assert.IsNull(contentControl.Content);
        }

        [Test]
        public void QueuedInitializationDoesNotAttachHostFromPreviousRenderOperation()
        {
            var contentControl = new ContentControl();
            var control = new WpfControl(contentControl);
            var browserHandle = new IntPtr(1);
            var previousRenderAttached = false;

            control.InitializeRender(browserHandle);
            contentControl.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => previousRenderAttached = contentControl.Content != null));
            control.DestroyRender();
            control.InitializeRender(browserHandle);

            DrainDispatcher(contentControl.Dispatcher);
            Assert.IsFalse(previousRenderAttached);
            Assert.IsNotNull(contentControl.Content);
        }

        private static void DrainDispatcher(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
