using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Shared.Helpers;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;

namespace CefGlue.Tests
{
    [TestFixture]
    public class MessageDispatcherTests
    {
        [Test]
        public void ConcurrentRegistrationsAreAllDispatched()
        {
            const int HandlerCount = 512;
            const string MessageName = "ConcurrentMessage";
            var dispatcher = new MessageDispatcher();
            var invocationCount = 0;

            Parallel.For(0, HandlerCount, index => dispatcher.RegisterMessageHandler(MessageName, args => Interlocked.Increment(ref invocationCount)));

            CefRuntime.Load();
            using (var message = CefProcessMessage.Create(MessageName))
            {
                dispatcher.DispatchMessage(null, null, CefProcessId.Browser, message);
            }

            Assert.AreEqual(HandlerCount, invocationCount);
        }

        [Test]
        public void NativeObjectUnregistrationMessageRoundTripsAsUnregistration()
        {
            const string ObjectName = "object-to-remove";
            CefRuntime.Load();
            var request = new Messages.NativeObjectUnregistrationRequest { ObjectName = ObjectName };

            using (var message = request.ToCefProcessMessage())
            {
                Messages.NativeObjectUnregistrationRequest result = Messages.NativeObjectUnregistrationRequest.FromCefMessage(message);

                Assert.AreEqual(ObjectName, result.ObjectName);
            }
        }
    }
}
