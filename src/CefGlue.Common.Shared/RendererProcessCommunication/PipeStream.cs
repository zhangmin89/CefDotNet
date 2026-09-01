using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Xilium.CefGlue.Common.Shared.RendererProcessCommunication
{
    internal class PipeStream
    {
        internal const int MaxMessageByteLength = 1024 * 1024;

        private readonly Stream _stream;
        private readonly UnicodeEncoding _streamEncoding;

        public PipeStream(Stream stream)
        {
            _stream = stream;
            _streamEncoding = new UnicodeEncoding();
        }

        public string ReadString()
        {
            var length = ReadInt(_stream);
            ValidateMessageLength(length);
            var buffer = new byte[length];
            _stream.ReadExactly(buffer, 0, buffer.Length);

            return _streamEncoding.GetString(buffer);
        }

        public async Task<string> ReadStringAsync(CancellationToken cancellationToken)
        {
            var length = await ReadIntAsync(_stream, cancellationToken).ConfigureAwait(false);
            ValidateMessageLength(length);
            var buffer = new byte[length];
            await _stream.ReadExactlyAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            return _streamEncoding.GetString(buffer);
        }

        public void WriteString(string message)
        {
            var byteLength = _streamEncoding.GetByteCount(message);
            ValidateMessageLength(byteLength);
            var buffer = new byte[byteLength];
            _streamEncoding.GetBytes(message, 0, message.Length, buffer, 0);
            WriteInt(_stream, byteLength);
            _stream.Write(buffer, 0, buffer.Length);
            _stream.Flush();
        }

        private static int ReadInt(Stream stream)
        {
            var valueInBytes = new byte[4];
            stream.ReadExactly(valueInBytes, 0, valueInBytes.Length);
            return BitConverter.ToInt32(valueInBytes, 0);
        }

        private static async Task<int> ReadIntAsync(Stream stream, CancellationToken cancellationToken)
        {
            var valueInBytes = new byte[4];
            await stream.ReadExactlyAsync(valueInBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            return BitConverter.ToInt32(valueInBytes, 0);
        }

        private static void ValidateMessageLength(int length)
        {
            if (length < 0 || length > MaxMessageByteLength || length % sizeof(char) != 0)
            {
                throw new InvalidDataException($"Invalid message byte length {length}. The length must be even and between 0 and {MaxMessageByteLength} bytes.");
            }
        }

        private static int WriteInt(Stream stream, int value)
        {
            var valueInBytes = BitConverter.GetBytes(value);
            foreach (var @byte in valueInBytes)
            {
                stream.WriteByte(@byte);
            }
            return valueInBytes.Length;
        }
    }
}
