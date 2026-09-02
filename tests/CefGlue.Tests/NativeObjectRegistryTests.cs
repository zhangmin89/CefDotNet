using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue.Common.ObjectBinding;

namespace CefGlue.Tests
{
    [TestFixture]
    public class NativeObjectRegistryTests
    {
        [Test]
        public void ConcurrentRegistrationOfSameNameSucceedsOnce()
        {
            const int RegistrationCount = 512;
            const string ObjectName = "sharedObject";
            var registry = new NativeObjectRegistry();
            var successfulRegistrations = 0;

            Parallel.For(0, RegistrationCount, index =>
            {
                if (registry.Register(new RegisteredObject(), ObjectName))
                {
                    Interlocked.Increment(ref successfulRegistrations);
                }
            });

            Assert.AreEqual(1, successfulRegistrations);
            Assert.AreEqual(ObjectName, registry.Get(ObjectName).Name);

            registry.Dispose();

            Assert.IsNull(registry.Get(ObjectName));
        }

        private sealed class RegisteredObject
        {
            public void Invoke()
            {
            }
        }
    }
}
