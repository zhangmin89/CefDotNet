namespace Xilium.CefGlue.Common.InternalHandlers
{
    internal sealed class CommonCefRequestHandler : CefRequestHandler
    {
        private readonly ICefBrowserHost _owner;

        public CommonCefRequestHandler(ICefBrowserHost owner)
        {
            _owner = owner;
        }

        protected override bool OnBeforeBrowse(CefBrowser browser, CefFrame frame, CefRequest request, bool userGesture, bool isRedirect)
        {
            return _owner.RequestHandler?.HandleBeforeBrowse(browser, frame, request, userGesture, isRedirect) ?? false;
        }

        protected override bool OnOpenUrlFromTab(CefBrowser browser, CefFrame frame, string targetUrl, CefWindowOpenDisposition targetDisposition, bool userGesture)
        {
            return _owner.RequestHandler?.HandleOpenUrlFromTab(browser, frame, targetUrl, targetDisposition, userGesture) ?? false;
        }

        protected override CefResourceRequestHandler GetResourceRequestHandler(CefBrowser browser, CefFrame frame, CefRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        {
            return _owner.RequestHandler?.HandleGetResourceRequestHandler(browser, frame, request, isNavigation, isDownload, requestInitiator, ref disableDefaultHandling);
        }

        protected override bool GetAuthCredentials(CefBrowser browser, string originUrl, bool isProxy, string host, int port, string realm, string scheme, CefAuthCallback callback)
        {
            return _owner.RequestHandler?.HandleGetAuthCredentials(browser, originUrl, isProxy, host, port, realm, scheme, callback) ?? false;
        }

        protected override bool OnCertificateError(CefBrowser browser, CefErrorCode certError, string requestUrl, CefSslInfo sslInfo, CefCallback callback)
        {
            return _owner.RequestHandler?.HandleCertificateError(browser, certError, requestUrl, sslInfo, callback) ?? false;
        }

        protected override bool OnSelectClientCertificate(CefBrowser browser, bool isProxy, string host, int port, CefX509Certificate[] certificates, CefSelectClientCertificateCallback callback)
        {
            return _owner.RequestHandler?.HandleSelectClientCertificate(browser, isProxy, host, port, certificates, callback) ?? false;
        }

        protected override void OnRenderViewReady(CefBrowser browser)
        {
            _owner.RequestHandler?.HandleRenderViewReady(browser);
        }

        protected override void OnRenderProcessTerminated(CefBrowser browser, CefTerminationStatus status)
        {
            _owner.HandleRenderProcessTerminated(browser);
            _owner.RequestHandler?.HandleRenderProcessTerminated(browser, status);
        }

        protected override void OnDocumentAvailableInMainFrame(CefBrowser browser)
        {
            _owner.RequestHandler?.HandleDocumentAvailableInMainFrame(browser);
        }
    }
}
