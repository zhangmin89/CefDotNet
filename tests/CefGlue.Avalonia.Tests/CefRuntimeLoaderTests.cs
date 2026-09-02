using System;
using NUnit.Framework;
using Xilium.CefGlue.Common;

namespace CefGlue.Tests
{
    [TestFixture]
    public class CefRuntimeLoaderTests : TestBase
    {
        [Test]
        public void InitializeRejectsCallsAfterRuntimeWasLoaded()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => CefRuntimeLoader.Initialize());

            Assert.AreEqual("CEF has already been initialized.", exception.Message);
        }
    }
}
