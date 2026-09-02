using System;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using NUnit.Framework;
using Xilium.CefGlue.WPF.Platform;

namespace CefGlue.WPF.Tests
{
    [TestFixture]
    [Apartment(ApartmentState.STA)]
    public class WpfOffScreenControlHostTests
    {
        [Test]
        public void ReplacingPendingTooltipQueuesOnlyLatestValue()
        {
            var control = new ContentControl();
            var host = new WpfOffScreenControlHost(control);
            var tooltip = GetPrivateField<ToolTip>(host, "_tooltip");
            var contentChanges = 0;
            EventHandler contentChanged = (sender, args) => contentChanges++;
            var contentDescriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ToolTip));
            contentDescriptor.AddValueChanged(tooltip, contentChanged);

            try
            {
                host.SetTooltip("first");
                host.SetTooltip("latest");
                RunDispatcherFor(control.Dispatcher, TimeSpan.FromMilliseconds(600));

                Assert.AreEqual(1, contentChanges);
                Assert.AreEqual("latest", tooltip.Content);
            }
            finally
            {
                contentDescriptor.RemoveValueChanged(tooltip, contentChanged);
                host.SetTooltip(null);
                DrainDispatcher(control.Dispatcher);
            }
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"Unable to find field '{fieldName}'.");
            return (T)field.GetValue(target);
        }

        private static void RunDispatcherFor(Dispatcher dispatcher, TimeSpan duration)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher) { Interval = duration };
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void DrainDispatcher(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
