using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;

namespace CefGlue.Tests
{
    [TestFixture]
    public class PipeStreamTests
    {
        private const int MaxMessageByteLength = 1024 * 1024;

        [Test]
        public void ReadStringReadsTheCompleteFrameAcrossPartialReads()
        {
            const string Message = "partial read 日本語";
            using (var serialized = new MemoryStream())
            {
                new PipeStream(serialized).WriteString(Message);

                using (var partial = new PartialReadStream(serialized.ToArray()))
                {
                    Assert.AreEqual(Message, new PipeStream(partial).ReadString());
                }
            }
        }

        [Test]
        public void ReadStringThrowsWhenLengthPrefixIsTruncated()
        {
            using (var stream = new MemoryStream(new byte[] { 1, 0, 0 }))
            {
                Assert.Throws<EndOfStreamException>(() => new PipeStream(stream).ReadString());
            }
        }

        [Test]
        public void ReadStringThrowsWhenPayloadIsTruncated()
        {
            byte[] frame;
            using (var serialized = new MemoryStream())
            {
                new PipeStream(serialized).WriteString("payload");
                frame = serialized.ToArray();
            }

            Array.Resize(ref frame, frame.Length - 1);
            using (var stream = new MemoryStream(frame))
            {
                Assert.Throws<EndOfStreamException>(() => new PipeStream(stream).ReadString());
            }
        }

        [Test]
        public void ReadStringAcceptsEmptyFrame()
        {
            using (var stream = new MemoryStream(BitConverter.GetBytes(0)))
            {
                Assert.AreEqual(string.Empty, new PipeStream(stream).ReadString());
            }
        }

        [Test]
        public void ReadStringRejectsNegativeLength()
        {
            using (var stream = new MemoryStream(BitConverter.GetBytes(-1)))
            {
                Assert.Throws<InvalidDataException>(() => new PipeStream(stream).ReadString());
            }
        }

        [Test]
        public void ReadStringRejectsFrameAboveLimitBeforeReadingPayload()
        {
            using (var stream = new MemoryStream(BitConverter.GetBytes(MaxMessageByteLength + 2)))
            {
                Assert.Throws<InvalidDataException>(() => new PipeStream(stream).ReadString());
            }
        }

        [Test]
        public void ReadStringRejectsOddUtf16ByteLength()
        {
            using (var stream = new MemoryStream(new byte[] { 1, 0, 0, 0, 65 }))
            {
                Assert.Throws<InvalidDataException>(() => new PipeStream(stream).ReadString());
            }
        }

        [Test]
        public void WriteStringRejectsPayloadAboveLimit()
        {
            using (var stream = new MemoryStream())
            {
                var message = new string('a', MaxMessageByteLength / sizeof(char) + 1);
                Assert.Throws<InvalidDataException>(() => new PipeStream(stream).WriteString(message));
            }
        }

        [Test]
        public void ReadStringAsyncHonorsCancellation()
        {
            using (var stream = new CancellationAwareReadStream())
            using (var cancellationTokenSource = new CancellationTokenSource())
            {
                var readTask = new PipeStream(stream).ReadStringAsync(cancellationTokenSource.Token);
                cancellationTokenSource.Cancel();

                Assert.ThrowsAsync<TaskCanceledException>(async () => await readTask);
            }
        }

        private sealed class CancellationAwareReadStream : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return new ValueTask<int>(WaitForCancellation(cancellationToken));
            }

            private static async Task<int> WaitForCancellation(CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class PartialReadStream : MemoryStream
        {
            public PartialReadStream(byte[] buffer) : base(buffer)
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return base.Read(buffer, offset, Math.Min(count, 1));
            }
        }
    }
}
