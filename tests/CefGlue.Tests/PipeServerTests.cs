using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;

namespace CefGlue.Tests
{
    [TestFixture]
    public class PipeServerTests
    {
        [Test]
        public async Task ReceivesMessageAndCanBeDisposedMoreThanOnce()
        {
            const string ExpectedMessage = "renderer process failure";
            var pipeName = CreatePipeName();
            var messageReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new PipeServer(pipeName);
            server.MessageReceived += message => messageReceived.TrySetResult(message);

            using (var client = new PipeClient(pipeName))
            {
                client.SendMessage(ExpectedMessage);
            }

            Assert.AreEqual(ExpectedMessage, await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.DoesNotThrow(server.Dispose);
            Assert.DoesNotThrow(server.Dispose);
        }

        [Test]
        public async Task ContinuesListeningAfterMalformedClientFrame()
        {
            var pipeName = CreatePipeName();
            var messageReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = new PipeServer(pipeName))
            {
                server.MessageReceived += message => messageReceived.TrySetResult(message);

                using (var malformedClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous))
                using (var connectionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    await malformedClient.ConnectAsync(connectionCancellation.Token);
                    await malformedClient.WriteAsync(BitConverter.GetBytes(-1), connectionCancellation.Token);
                    await malformedClient.FlushAsync(connectionCancellation.Token);
                }

                const string ExpectedMessage = "valid-message";
                using (var client = new PipeClient(pipeName))
                {
                    client.SendMessage(ExpectedMessage);
                }

                Assert.AreEqual(ExpectedMessage, await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        private static string CreatePipeName()
        {
            return $"CefGlue.Tests.{Guid.NewGuid():N}";
        }
    }
}
