using CefGlue.Tests.CustomSchemes;
using CefGlue.Tests.Helpers;
using NUnit.Framework;
using System;
using System.Text;
using System.Threading.Tasks;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Events;

namespace CefGlue.Tests
{
    public static class BrowserExtensions
    {
        public static Task LoadContent(this AvaloniaCefBrowser browser, string content)
        {
            var loadTask = browser.AwaitLoad();

            var url = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(UTF8Encoding.UTF8.GetBytes(content));
            TestDiagnostics.Write(TestContext.CurrentContext.Test.FullName, "navigation-requested", $"urlLength={url.Length}");
            browser.Address = url;
            TestDiagnostics.Write(TestContext.CurrentContext.Test.FullName, "navigation-address-set");
            return loadTask;
        }

        public static Task AwaitLoad(this AvaloniaCefBrowser browser)
        {
            var testName = TestContext.CurrentContext.Test.FullName;
            var taskCompletionSource = new TaskCompletionSource<bool>();
            TestDiagnostics.Write(testName, "load-await-start");

            void UnsubcribeEvents()
            {
                browser.LoadEnd -= OnBrowserLoadEnd;
                browser.LoadError -= OnBrowserLoadError;
            }

            void OnBrowserLoadError(object sender, LoadErrorEventArgs e)
            {
                TestDiagnostics.Write(testName, "load-error-received", $"frame={e.Frame.Identifier} main={e.Frame.IsMain}");
                UnsubcribeEvents();
                taskCompletionSource.SetException(new Exception(e.ErrorText));
            }

            void OnBrowserLoadEnd(object sender, LoadEndEventArgs e)
            {
                TestDiagnostics.Write(testName, "load-end-received", $"frame={e.Frame.Identifier} main={e.Frame.IsMain} urlLength={e.Frame.Url.Length}");
                if (e.Frame.Url.StartsWith("data:") || e.Frame.Url.StartsWith(CustomSchemeHandlerFactory.SchemeName + ":"))
                {
                    UnsubcribeEvents();
                    TestDiagnostics.Write(testName, "load-await-completing", $"frame={e.Frame.Identifier}");
                    taskCompletionSource.SetResult(true);
                    TestDiagnostics.Write(testName, "load-end-handler-returning", $"frame={e.Frame.Identifier}");
                }
            }

            browser.LoadEnd += OnBrowserLoadEnd;
            browser.LoadError += OnBrowserLoadError;
            return taskCompletionSource.Task;
        }
    }
}
