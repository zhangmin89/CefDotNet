using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Xilium.CefGlue.Common.Shared.RendererProcessCommunication
{
    internal class PipeServer : IDisposable
    {
        private const int MaxErrorsAllowed = 5;
        private static readonly TimeSpan ClientReadTimeout = TimeSpan.FromSeconds(10);

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private int _disposed;

        public event Action<string> MessageReceived;

        public PipeServer(string pipeName)
        {
            var cancellationToken = _cancellationTokenSource.Token;
            _ = Task.Run(() => ListenAsync(pipeName, cancellationToken));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // release the MessageReceived handlers to prevent any possible memory leak
            MessageReceived = null;
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }

        private async Task ListenAsync(string pipeName, CancellationToken cancellationToken)
        {
            var errorCount = 0;
            // Keep the Unix listening socket alive so queued clients survive a disconnect.
            using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await serverPipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        using (var clientReadCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            clientReadCancellationTokenSource.CancelAfter(ClientReadTimeout);
                            try
                            {
                                await HandleClientConnectedAsync(serverPipe, clientReadCancellationTokenSource.Token).ConfigureAwait(false);
                            }
                            catch (InvalidDataException) when (!cancellationToken.IsCancellationRequested)
                            {
                            }
                            catch (IOException) when (!cancellationToken.IsCancellationRequested)
                            {
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                            }
                        }
                    }
                    finally
                    {
                        serverPipe.Disconnect();
                    }

                    errorCount = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    errorCount++;
                    if (errorCount > MaxErrorsAllowed)
                    {
                        break;
                    }
                }
            }
        }

        private async Task HandleClientConnectedAsync(Stream pipe, CancellationToken cancellationToken)
        {
            var messageReceivedHandler = MessageReceived;
            if (messageReceivedHandler == null)
            {
                return;
            }

            var stream = new PipeStream(pipe);
            var message = await stream.ReadStringAsync(cancellationToken).ConfigureAwait(false);
            messageReceivedHandler(message);
        }
    }
}
