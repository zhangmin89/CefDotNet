using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CefGlue.Tests;
using NUnit.Framework;
using Xilium.CefGlue.Common.Helpers;
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

        [Test]
        public void CloseContextMenuRequestsClosureAndClosedEventCancels()
        {
            var contentControl = new ContentControl();
            var control = new WpfControl(contentControl);
            using (var callback = new CefRunContextMenuCallbackHarness())
            {
                control.OpenContextMenu(new[] { new MenuEntry { Label = "Action", IsEnabled = true, CommandId = 42 } }, 0, 0, callback.Callback);
                DrainDispatcher(contentControl.Dispatcher);
                var menu = contentControl.ContextMenu;

                control.CloseContextMenu();
                DrainDispatcher(contentControl.Dispatcher);

                Assert.IsFalse(menu.IsOpen);
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent, menu));
                Assert.IsNull(contentControl.ContextMenu);
                Assert.AreEqual(1, callback.CancelCount);
            }
        }

        [Test]
        public void SelectingContextMenuItemContinuesWithoutCanceling()
        {
            var contentControl = new ContentControl();
            var control = new WpfControl(contentControl);
            using (var callback = new CefRunContextMenuCallbackHarness())
            {
                control.OpenContextMenu(new[] { new MenuEntry { Label = "Action", IsEnabled = true, CommandId = 42 } }, 0, 0, callback.Callback);
                DrainDispatcher(contentControl.Dispatcher);

                var menu = contentControl.ContextMenu;
                var menuItem = (MenuItem)menu.Items[0];
                menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menuItem));
                menu.IsOpen = false;
                DrainDispatcher(contentControl.Dispatcher);

                Assert.AreEqual(1, callback.ContinueCount);
                Assert.AreEqual(0, callback.CancelCount);
                Assert.AreEqual(42, callback.LastCommandId);
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
