using System.Threading.Tasks;
using Avalonia;
using NUnit.Framework;
using Xilium.CefGlue.Avalonia.Platform;

namespace CefGlue.Tests
{
    [TestFixture]
    public class AvaloniaPopupTests : TestBase
    {
        [Test]
        public async Task DisposePreventsQueuedOpenAndResizeOperations()
        {
            ExtendedAvaloniaPopup popup = null;
            AvaloniaPopup host = null;
            await Run(() =>
            {
                popup = new ExtendedAvaloniaPopup
                {
                    PlacementTarget = Browser,
                    Width = 1,
                    Height = 2,
                    Position = new PixelPoint(3, 4)
                };
                host = new AvaloniaPopup(popup, popup.VisualChildren);

                host.Open();
                host.MoveAndResize(10, 20, 30, 40);
                Assert.DoesNotThrow(host.Dispose);
                Assert.DoesNotThrow(host.Dispose);
            });
            await Run(() => { });

            await Run(() =>
            {
                Assert.IsFalse(popup.IsVisible);
                Assert.AreEqual(1, popup.Width);
                Assert.AreEqual(2, popup.Height);
                Assert.AreEqual(new PixelPoint(3, 4), popup.Position);
                host.RenderSurface.Dispose();
                popup.Close();
            });
        }
    }
}
