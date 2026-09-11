using Xilium.CefGlue.Common.Shared.Helpers;

namespace Xilium.CefGlue.Common.InternalHandlers
{
    internal class CommonCefLoadHandler : CefLoadHandler
    {
        private readonly ICefBrowserHost _owner;

        public CommonCefLoadHandler(ICefBrowserHost owner)
        {
            _owner = owner;
        }

        protected override void OnLoadingStateChange(CefBrowser browser, bool isLoading, bool canGoBack, bool canGoForward)
        {
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, 0, "browser-loading-state-received", $"browser={browser.Identifier} loading={isLoading}");
            }
            _owner.HandleLoadingStateChange(browser, isLoading, canGoBack, canGoForward);
        }

        protected override void OnLoadError(CefBrowser browser, CefFrame frame, CefErrorCode errorCode, string errorText, string failedUrl)
        {
            var frameIdentifier = JavascriptExecutionTrace.IsEnabled ? frame.Identifier : 0;
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, frameIdentifier, "browser-load-error-received", $"browser={browser.Identifier} main={frame.IsMain} error={errorCode} urlLength={failedUrl.Length}");
            }
            _owner.HandleLoadError(browser, frame, errorCode, errorText, failedUrl);
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, frameIdentifier, "browser-load-error-dispatched", "");
            }
        }

        protected override void OnLoadStart(CefBrowser browser, CefFrame frame, CefTransitionType transitionType)
        {
            var frameIdentifier = JavascriptExecutionTrace.IsEnabled ? frame.Identifier : 0;
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, frameIdentifier, "browser-load-start-received", $"browser={browser.Identifier} main={frame.IsMain} urlLength={frame.Url.Length} transition={transitionType}");
            }
            _owner.HandleLoadStart(browser, frame, transitionType);
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, frameIdentifier, "browser-load-start-dispatched", "");
            }
        }

        protected override void OnLoadEnd(CefBrowser browser, CefFrame frame, int httpStatusCode)
        {
            var frameIdentifier = JavascriptExecutionTrace.IsEnabled ? frame.Identifier : 0;
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, frameIdentifier, "browser-load-end-received", $"browser={browser.Identifier} main={frame.IsMain} urlLength={frame.Url.Length} status={httpStatusCode}");
            }
            _owner.HandleLoadEnd(browser, frame, httpStatusCode);
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(0, frameIdentifier, "browser-load-end-dispatched", "");
            }
        }
    }
}
