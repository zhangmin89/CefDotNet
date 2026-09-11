using CefGlue.Tests.Helpers;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Events;
using Xilium.CefGlue.Common.Handlers;

namespace CefGlue.Tests.Network
{
    public class NetworkTests : TestBase
    {
        private class TestsRequestHandler : RequestHandler
        {
            private readonly string _testName = TestContext.CurrentContext.Test.FullName;
            private readonly TestsResourceRequestHandler _resourceRequestHandler;

            public TestsRequestHandler(Func<CefRequest, DefaultResourceHandler> resourceHandler)
            {
                _resourceRequestHandler = new TestsResourceRequestHandler(resourceHandler);
            }

            protected override CefResourceRequestHandler GetResourceRequestHandler(CefBrowser browser, CefFrame frame, CefRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
            {
                TestDiagnostics.Write(_testName, "request-handler-called");
                return _resourceRequestHandler;
            }
        }

        private class TestsResourceRequestHandler : CefResourceRequestHandler
        {
            private readonly string _testName = TestContext.CurrentContext.Test.FullName;
            private readonly Func<CefRequest, DefaultResourceHandler> _resourceHandler;

            public TestsResourceRequestHandler(Func<CefRequest, DefaultResourceHandler> resourceHandler)
            {
                _resourceHandler = resourceHandler;
            }

            protected override CefCookieAccessFilter GetCookieAccessFilter(CefBrowser browser, CefFrame frame, CefRequest request)
            {
                return null;
            }

            protected override CefResourceHandler GetResourceHandler(CefBrowser browser, CefFrame frame, CefRequest request)
            {
                TestDiagnostics.Write(_testName, "resource-handler-start");
                var handler = _resourceHandler(request);
                TestDiagnostics.Write(_testName, "resource-handler-returned");
                return handler;
            }
        }

        private class TestsResourceHandler : DefaultResourceHandler
        {
            public void Read()
            {
                Read(new MemoryStream(), 10, out var bytesRead, null);
            }
        }

        private class CustomStream : Stream
        {
            public event Action ReadStarted;

            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => true;

            public override long Length => 1000;

            public override long Position { get; set; }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadStarted?.Invoke();
                Position += count;
                return count;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();

            public override void SetLength(long value) { }

            public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
        }

        private class Response
        {
            public string AllowOrigin { get; set; }
            public string Status { get; set; }
            public string Data { get; set; }
            public string RedirectUrl { get; set; }
        }

        private async Task<Response> GetResponse(Func<CefRequest, DefaultResourceHandler> resourceHandler)
        {
            var testName = TestContext.CurrentContext.Test.FullName;
            TestDiagnostics.Write(testName, "response-start");
            TestDiagnostics.Write(testName, "page-load-start");
            await Browser.LoadContent("<html/>").WaitAsync(TimeSpan.FromSeconds(10));
            TestDiagnostics.Write(testName, "page-load-complete");
            Browser.RequestHandler = new TestsRequestHandler(resourceHandler);
            var taskCompletion = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);

            Browser.ConsoleMessage += OnConsoleMessage;
            TestDiagnostics.Write(testName, "console-handler-attached");

            void OnConsoleMessage(object sender, ConsoleMessageEventArgs message)
            {
                TestDiagnostics.Write(testName, "console-message-received", message.Message);
                Browser.ConsoleMessage -= OnConsoleMessage;
                var messageParts = message.Message.Split("|");
                if (messageParts.Length == 4)
                {
                    taskCompletion.SetResult(new Response()
                    {
                        AllowOrigin = messageParts[0],
                        Status = messageParts[1],
                        RedirectUrl = messageParts[2],
                        Data = messageParts[3]
                    });
                }
                else
                {
                    taskCompletion.SetResult(new Response()
                    {
                        Data = message.Message
                    });
                }
                TestDiagnostics.Write(testName, "response-signalled");
            }

            var script = 
                "fetch('https://tests/resource').then(response => {" +
                "   let result = [ response.headers.get('Access-Control-Allow-Origin'), response.status, response.url ];" +
                "   if (response.status === 200) {" +
                "       return response.text().then(data => console.log(result.concat([ data ]).join('|')));" +
                "   }" +
                "   console.log(result.concat([ '' ]).join('|'));" +
                "}).catch(error => console.log(String(error)))";
            try
            {
                TestDiagnostics.Write(testName, "fetch-script-start");
                await EvaluateJavascript<int>(script).WaitAsync(TimeSpan.FromSeconds(10));
                TestDiagnostics.Write(testName, "fetch-script-complete");
                TestDiagnostics.Write(testName, "response-wait");
                var response = await taskCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                TestDiagnostics.Write(testName, "response-complete");
                return response;
            }
            finally
            {
                Browser.ConsoleMessage -= OnConsoleMessage;
            }
        }

        [Test]
        public async Task ResourceHandlerIsCalledWithStatusOk()
        {
            const string Data = "test";
            var response = await GetResponse(_ =>
            {
                var handler = new DefaultResourceHandler();
                handler.Response = StreamHelper.GetStream(Data);
                return handler;
            });

            Assert.AreEqual("*", response.AllowOrigin);
            Assert.AreEqual("200", response.Status);
            Assert.AreEqual(Data, response.Data);
        }

        [Test]
        public async Task ResourceHandlerIsCalledWithError()
        {
            var testName = TestContext.CurrentContext.Test.FullName;
            TestDiagnostics.Write(testName, "test-body-start");
            var response = await GetResponse(_ => new DefaultResourceHandler());

            StringAssert.Contains("Error", response.Data);
            TestDiagnostics.Write(testName, "test-body-complete");
        }

        [Test]
        public async Task ResourceHandlerWithRedirectUrl()
        {
            const string RedirectUrl = "http://test/otherurl";

            var response = await GetResponse(request =>
            {
                var handler = new DefaultResourceHandler();
                if (request.Url == RedirectUrl)
                {
                    handler.Response = StreamHelper.GetStream("ok");
                }
                else if (!request.Url.StartsWith("data:"))
                {
                    handler.RedirectUrl = RedirectUrl;
                }
                return handler;
            });

            Assert.IsNotNull(response.RedirectUrl, $"Fetch did not produce a redirect response: {response.Data}");
            StringAssert.Contains(RedirectUrl, response.RedirectUrl);
        }

        [Test]
        public async Task ResourceHandlerStreamsAreNotReadAtSameTime()
        {
            var checkPoints = new List<int>();

            var stream = new CustomStream();
            var handler1 = new TestsResourceHandler();
            var handler2 = new TestsResourceHandler();

            // both handlers share the same stream
            handler1.Response = stream;
            handler2.Response = stream;

            Task concurrentReadTask = null;

            void OnStreamReadStarted()
            {
                checkPoints.Add(2);
                stream.ReadStarted -= OnStreamReadStarted;

                var concurrentReadTaskStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                concurrentReadTask = Task.Run(() =>
                {
                    checkPoints.Add(3);
                    concurrentReadTaskStarted.SetResult(true);
                    handler2.Read();
                    checkPoints.Add(5);
                });
                concurrentReadTaskStarted.Task.Wait();
                // simulate read with 1s duration
                Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                checkPoints.Add(4);
            }

            stream.ReadStarted += () => Assert.AreEqual(0, stream.Position, "Streams Position on Reads must be 0");

            stream.ReadStarted += OnStreamReadStarted;

            checkPoints.Add(1);
            handler1.Read();

            Assert.IsNotNull(concurrentReadTask);
            await concurrentReadTask;
            checkPoints.Add(6);

            CollectionAssert.AreEqual(Enumerable.Range(1, 6), checkPoints);
        }
    }
}
