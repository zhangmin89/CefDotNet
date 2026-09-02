using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
            using (var callback = new CefRunContextMenuCallbackHarness())
            {
                await Run(() =>
                {
                    var control = new AvaloniaControl(Browser, new AvaloniaList<Visual>());
                    control.OpenContextMenu(new[] { new MenuEntry { Label = "Action", IsEnabled = true, CommandId = 42 } }, 0, 0, callback.Callback);
                });
                await Run(() => { });

                await Run(() =>
                {
                    var menu = Browser.ContextMenu;
                    var menuItem = (MenuItem)menu.Items.Cast<object>().Single();
                    menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.Close();
                });
                await Run(() => { });

                Assert.AreEqual(1, callback.ContinueCount);
                Assert.AreEqual(0, callback.CancelCount);
                Assert.AreEqual(42, callback.LastCommandId);
            }
        }
    }
}
