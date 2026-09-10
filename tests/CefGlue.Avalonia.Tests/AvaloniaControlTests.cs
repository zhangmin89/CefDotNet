using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CefGlue.Tests.Helpers;
using NUnit.Framework;
using Xilium.CefGlue.Avalonia.Platform;
using Xilium.CefGlue.Common.Helpers;

namespace CefGlue.Tests
{
    [TestFixture]
    public class AvaloniaControlTests : TestBase
    {
        [Test]
        public async Task SelectingContextMenuItemContinuesWithoutCanceling()
        {
            var testName = TestContext.CurrentContext.Test.FullName;
            TestDiagnostics.Write(testName, "menu-open-queued");
            using (var callback = new CefRunContextMenuCallbackHarness())
            {
                await Run(() =>
                {
                    TestDiagnostics.Write(testName, "menu-open-start");
                    var control = new AvaloniaControl(Browser, new AvaloniaList<Visual>());
                    control.OpenContextMenu(new[] { new MenuEntry { Label = "Action", IsEnabled = true, CommandId = 42 } }, 0, 0, callback.Callback);
                    TestDiagnostics.Write(testName, "menu-open-returned");
                });
                await Run(() => { });

                await Run(() =>
                {
                    TestDiagnostics.Write(testName, "menu-click-start");
                    var menu = Browser.ContextMenu;
                    var menuItem = (MenuItem)menu.Items.Cast<object>().Single();
                    menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.Close();
                    TestDiagnostics.Write(testName, "menu-close-returned");
                });
                await Run(() => { });

                TestDiagnostics.Write(testName, "menu-drained");
                Assert.AreEqual(1, callback.ContinueCount);
                Assert.AreEqual(0, callback.CancelCount);
                Assert.AreEqual(42, callback.LastCommandId);
            }
        }
    }
}
