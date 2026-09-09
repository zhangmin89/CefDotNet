using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia.Platform;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.InternalHandlers;

namespace CefGlue.Tests;

[TestFixture, NonParallelizable, Category("OsrReviewFix")]
[Explicit("CEF rendering mode is process-wide; run this fixture separately from the windowed fixtures.")]
public class OsrBrowserReviewFixTests : BrowserReviewFixTests
{
    protected override bool WindowlessRenderingEnabled => true;

    [Test]
    public async Task WindowlessPopupCannotOverwriteMainBitmap()
    {
        var popup = await OpenPopup(true);
        Assert.IsTrue(popup.GetHost().IsWindowRenderingDisabled);
        popup.GetMainFrame().ExecuteJavaScript("document.body.style.margin='0'; document.body.style.background='red'; console.log('review-popup-ready');", "", 1);
        await display.Message("review-popup-ready").WaitAsync(Deadline);
        popup.GetHost().Invalidate(CefPaintElementType.View);
        await Task.Delay(Deadline);
        uint pixel = 0;
        await Run(() =>
        {
            var control = (AvaloniaOffScreenControlHost)typeof(CommonBrowserAdapter).GetProperty("Control", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Adapter)!;
            pixel = CenterPixel((WriteableBitmap)GetField(control.RenderSurface, "_bitmap")!);
        });
        Assert.AreEqual("rgb(0, 0, 255)", await EvaluateJavascript<string>("return getComputedStyle(document.body).backgroundColor;", Deadline));
        Assert.AreEqual(0xFF0000FFu, pixel, "The main surface must keep the main page's blue pixels.");
    }

    [Test]
    public async Task MainBrowserPopupMenuStillReceivesSizeVisibilityAndPixels()
    {
        var proxy = new CommonCefRenderHandler((IOffscreenCefBrowserHost)Adapter, new Xilium.CefGlue.Common.Helpers.Logger.NullLogger());
        var popup = (AvaloniaPopup)GetField(Adapter, "<Popup>k__BackingField")!;
        var window = (ExtendedAvaloniaPopup)GetField(popup, "_popup")!;
        await CefUi(() =>
        {
            Invoke(proxy, "OnPopupSize", nativeBrowser, new CefRectangle(0, 0, 8, 8));
            Invoke(proxy, "OnPopupShow", nativeBrowser, true);
        });
        var visible = false;
        await Run(() => visible = window.IsVisible);
        Assert.IsTrue(visible);
        Assert.AreEqual(8, popup.RenderSurface.Width);
        Assert.AreEqual(8, popup.RenderSurface.Height);
        var width = popup.RenderSurface.ScaledWidth;
        var height = popup.RenderSurface.ScaledHeight;
        var pixels = new int[width * height];
        Array.Fill(pixels, unchecked((int)0xFF00FF00));
        var buffer = Marshal.AllocHGlobal(pixels.Length * sizeof(int));
        uint actual = 0;
        try
        {
            Marshal.Copy(pixels, 0, buffer, pixels.Length);
            await CefUi(() => Invoke(proxy, "OnPaint", nativeBrowser, CefPaintElementType.Popup, new[] { new CefRectangle(0, 0, width, height) }, buffer, width, height));
            await Run(() => actual = CenterPixel((WriteableBitmap)GetField(popup.RenderSurface, "_bitmap")!));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            await CefUi(() => Invoke(proxy, "OnPopupShow", nativeBrowser, false));
            await Run(() => visible = window.IsVisible);
        }
        Assert.AreEqual(0xFF00FF00u, actual);
        Assert.IsFalse(visible);
    }

    [Test]
    public async Task MissingScreenCoordinateConversionReportsFailure()
    {
        var proxy = new CommonCefRenderHandler((IOffscreenCefBrowserHost)Adapter, new Xilium.CefGlue.Common.Helpers.Logger.NullLogger());
        var converted = true;
        await CefUi(() => converted = (bool)Invoke(proxy, "GetScreenPoint", nativeBrowser, 12, 34, 0, 0)!);
        Assert.IsFalse(converted);
    }
}
