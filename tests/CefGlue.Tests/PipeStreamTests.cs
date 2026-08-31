using System;
using System.IO;
using NUnit.Framework;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;

namespace CefGlue.Tests
{
    [TestFixture]
    public class PipeStreamTests
    {
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
