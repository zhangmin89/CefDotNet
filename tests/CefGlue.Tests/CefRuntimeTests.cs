using System;
using NUnit.Framework;
using Xilium.CefGlue;

namespace CefGlue.Tests
{
    [TestFixture]
    public class CefRuntimeTests
    {
        [TestCase(null)]
        [TestCase("")]
        public void AddCrossOriginWhitelistEntryRejectsMissingSourceOrigin(string sourceOrigin)
        {
            var exception = Assert.Throws<ArgumentNullException>(() => CefRuntime.AddCrossOriginWhitelistEntry(sourceOrigin, "https", "example.com", false));
            Assert.AreEqual("sourceOrigin", exception.ParamName);
        }

        [TestCase(null)]
        [TestCase("")]
        public void AddCrossOriginWhitelistEntryRejectsMissingTargetProtocol(string targetProtocol)
        {
            var exception = Assert.Throws<ArgumentNullException>(() => CefRuntime.AddCrossOriginWhitelistEntry("https://source.example.com", targetProtocol, "example.com", false));
            Assert.AreEqual("targetProtocol", exception.ParamName);
        }

        [TestCase(null)]
        [TestCase("")]
        public void RemoveCrossOriginWhitelistEntryRejectsMissingSourceOrigin(string sourceOrigin)
        {
            var exception = Assert.Throws<ArgumentNullException>(() => CefRuntime.RemoveCrossOriginWhitelistEntry(sourceOrigin, "https", "example.com", false));
            Assert.AreEqual("sourceOrigin", exception.ParamName);
        }

        [TestCase(null)]
        [TestCase("")]
        public void RemoveCrossOriginWhitelistEntryRejectsMissingTargetProtocol(string targetProtocol)
        {
            var exception = Assert.Throws<ArgumentNullException>(() => CefRuntime.RemoveCrossOriginWhitelistEntry("https://source.example.com", targetProtocol, "example.com", false));
            Assert.AreEqual("targetProtocol", exception.ParamName);
        }
    }
}
