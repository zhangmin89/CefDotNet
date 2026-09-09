using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CefGlue.Tests.CustomSchemes;
using CefGlue.Tests.Helpers;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Shared;

namespace CefGlue.Tests
{
#if !DEBUG
    [CancelAfter(30000)]
#endif
    public class TestBase
    {
        private static object initLock = new object();
        private static bool initialized = false;
        protected static readonly string CacheRoot = Path.Combine(Path.GetTempPath(), "CefGlue.Tests", Guid.NewGuid().ToString("N"));

        private AvaloniaCefBrowser browser;
        private Window window;

        protected AvaloniaCefBrowser Browser => browser;

        protected virtual bool WindowlessRenderingEnabled => false;

        [OneTimeSetUp]
        protected async Task SetUp()
        {
            if (initialized)
            {
                return;
            }

            var initializationTaskCompletionSource = new TaskCompletionSource<bool>();

            lock (initLock)
            {
                if (initialized)
                {
                    return;
                }

                var uiThread = new Thread(() =>
                {
                    InitializeApplication(WindowlessRenderingEnabled);

                    Dispatcher.UIThread.Post(() =>
                    {
                        initialized = true;
                        initializationTaskCompletionSource.SetResult(true);
                    });
                    Dispatcher.UIThread.MainLoop(CancellationToken.None);
                });
                uiThread.IsBackground = true;
                uiThread.Start();
            }

            await initializationTaskCompletionSource.Task;
        }

        internal static void InitializeApplication(bool windowlessRenderingEnabled = false)
        {
            CefRuntimeLoader.Initialize(settings: new Xilium.CefGlue.CefSettings { WindowlessRenderingEnabled = windowlessRenderingEnabled, RootCachePath = CacheRoot, LogFile = Path.Combine(AppContext.BaseDirectory, "cef-tests.log") }, customSchemes: new[] {
                new CustomScheme()
                {
                    SchemeName = CustomSchemeHandlerFactory.SchemeName,
                    SchemeHandlerFactory = new CustomSchemeHandlerFactory()
                }
            });
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            initialized = true;
        }

        [SetUp]
        protected virtual async Task Setup()
        {
            var testName = TestContext.CurrentContext.Test.FullName;
            TestDiagnostics.Write(testName, "setup-start");
            await InternalSetup(() => new AvaloniaCefBrowser());

            TestDiagnostics.Write(testName, "extra-setup-start");
            await ExtraSetup();
            TestDiagnostics.Write(testName, "setup-complete");
        }

        protected async Task InternalSetup(Func<AvaloniaCefBrowser> avaloniaCefBrowserFactory)
        {
            var testName = TestContext.CurrentContext.Test.FullName; // capture test name outside the async part (otherwise wont work properly)
            TestDiagnostics.Write(testName, "ui-setup-queued");
            await Run(async () =>
            {
                TestDiagnostics.Write(testName, "ui-setup-start");
                if (window == null)
                {
                    window = new Window();
                    window.Width = 1;
                    window.Height = 1;

                    window.Show();
                    TestDiagnostics.Write(testName, "window-shown");
                }

                window.Title = testName;

                var browserInitTaskCompletionSource = new TaskCompletionSource<bool>();
                browser = avaloniaCefBrowserFactory();
                TestDiagnostics.Write(testName, "browser-created");
                browser.BrowserInitialized += delegate ()
                {
                    TestDiagnostics.Write(testName, "browser-initialized");
                    browserInitTaskCompletionSource.SetResult(true);
                };

                window.Content = browser;

                TestDiagnostics.Write(testName, "browser-initialization-wait");
                await browserInitTaskCompletionSource.Task;
                TestDiagnostics.Write(testName, "ui-setup-complete");
            });
        }

        protected virtual Task ExtraSetup()
        {
            return Task.CompletedTask;
        }

        [TearDown] 
        protected void TearDown()
        {
            var testName = TestContext.CurrentContext.Test.FullName;
            TestDiagnostics.Write(testName, "teardown-start");
            browser?.Dispose();
            TestDiagnostics.Write(testName, "teardown-complete");
        }

        [OneTimeTearDown]
        protected async Task OneTimeTearDown()
        {
            var testName = TestContext.CurrentContext.Test.FullName;
            TestDiagnostics.Write(testName, "fixture-teardown-queued");
            await Run(() => {
                TestDiagnostics.Write(testName, "fixture-teardown-start");
                window?.Close();
                window = null;
                TestDiagnostics.Write(testName, "fixture-teardown-complete");
            });
        }

        protected Task Run(Func<Task> func) => Dispatcher.UIThread.InvokeAsync(func, DispatcherPriority.Background);

        protected Task Run(Action action) => Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background).GetTask();

        protected Task<T> EvaluateJavascript<T>(string script, TimeSpan? timeout = null) => Browser.EvaluateJavaScript<T>(script, timeout: timeout);
    }
}
