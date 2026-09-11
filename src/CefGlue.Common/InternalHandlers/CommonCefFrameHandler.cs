using System;
using System.Collections.Generic;
using System.Text;
using Xilium.CefGlue.Common.Shared.Helpers;

namespace Xilium.CefGlue.Common.InternalHandlers
{
    internal class CommonCefFrameHandler : CefFrameHandler
    {
        private readonly ICefBrowserHost _owner;

        public CommonCefFrameHandler(ICefBrowserHost owner)
        {
            _owner = owner;
        }

        protected override void OnFrameCreated(CefBrowser browser, CefFrame frame)
        {
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, frame.Identifier, "browser-frame-created", $"browser={browser.Identifier}");
            }
            base.OnFrameCreated(browser, frame);
        }

        protected override void OnFrameAttached(CefBrowser browser, CefFrame frame, bool reattached)
        {
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, frame.Identifier, "browser-frame-attached", $"browser={browser.Identifier} reattached={reattached}");
            }
            base.OnFrameAttached(browser, frame, reattached);
        }

        protected override void OnFrameDetached(CefBrowser browser, CefFrame frame)
        {
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, frame.Identifier, "browser-frame-detached", $"browser={browser.Identifier}");
            }
            base.OnFrameDetached(browser, frame);
            _owner.HandleFrameDetached(browser, frame);
        }
    }
}
