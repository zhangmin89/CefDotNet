using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue.Common.Shared.Helpers;

namespace CefGlue.Tests
{
    [TestFixture]
    public class AtomicIdGeneratorTests
    {
        [Test]
        public void GetNextReturnsUniqueSequentialIdsWhenCalledConcurrently()
        {
            const int Count = 4096;
            var generator = new AtomicIdGenerator();
            var ids = new int[Count];

            Parallel.For(0, Count, index => ids[index] = generator.GetNext());

            Array.Sort(ids);
            CollectionAssert.AreEqual(Enumerable.Range(0, Count), ids);
        }
    }
}
