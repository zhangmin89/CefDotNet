using System;
using System.IO.Pipes;

namespace Xilium.CefGlue.Common.Shared.RendererProcessCommunication
{
    internal class PipeClient : IDisposable {

        private readonly NamedPipeClientStream _pipe;

        public PipeClient(string pipeName)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None);
            try
            {
                pipe.Connect((int) TimeSpan.FromSeconds(10).TotalMilliseconds);
                _pipe = pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        public void SendMessage(string message)
        {
            var stream = new PipeStream(_pipe);
            stream.WriteString(message);
        }

        public void Dispose()
        {
            _pipe.Close();
            _pipe.Dispose();
        }
    }
}
