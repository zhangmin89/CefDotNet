using System;
using System.Reflection;
using NUnit.Framework;
using Xilium.CefGlue;

namespace CefGlue.Tests
{
    [TestFixture]
    public class CefRuntimeTests
    {
        [Test]
        public void ParseJsonAndReturnErrorReturnsErrorForInvalidJson()
        {
            CefRuntime.Load();

            var result = CefRuntime.ParseJsonAndReturnError("{", CefJsonParserOptions.Rfc, out var errorMessage);

            Assert.IsNull(result);
            Assert.IsNotEmpty(errorMessage);
        }

        [Test]
        public void ParseJsonAndReturnErrorParsesValidJson()
        {
            CefRuntime.Load();

            using var result = CefRuntime.ParseJsonAndReturnError("{\"value\":true}", CefJsonParserOptions.Rfc, out var errorMessage);

            Assert.IsNotNull(result);
            Assert.IsEmpty(errorMessage);
        }

        [TestCase(-1, 0)]
        [TestCase(0, -1)]
        [TestCase(5, 0)]
        [TestCase(3, 2)]
        [TestCase(int.MaxValue, 0)]
        public void Base64EncodeRejectsInvalidArrayRange(int offset, int length)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CefRuntime.Base64Encode(new byte[4], offset, length));
        }

        [Test]
        public void Base64EncodeEncodesValidArrayRanges()
        {
            CefRuntime.Load();
            var bytes = new byte[] { 1, 2, 3, 4, 5 };

            Assert.AreEqual(Convert.ToBase64String(bytes, 1, 3), CefRuntime.Base64Encode(bytes, 1, 3));
            Assert.IsNull(CefRuntime.Base64Encode(bytes, 0, 0));
            Assert.IsNull(CefRuntime.Base64Encode(Array.Empty<byte>(), 0, 0));
            Assert.IsNull(CefRuntime.Base64Encode(bytes, bytes.Length, 0));
        }

        [Test]
        [NonParallelizable]
        public void InitializeRejectsCallsAfterShutdown()
        {
            var shutdownField = typeof(CefRuntime).GetField("_shutdown", BindingFlags.NonPublic | BindingFlags.Static) ??
                throw new InvalidOperationException("Unable to inspect the CefRuntime shutdown state.");
            var originalValue = (bool)shutdownField.GetValue(null)!;

            try
            {
                shutdownField.SetValue(null, true);

                var exception = Assert.Throws<InvalidOperationException>(() => CefRuntime.Initialize(new CefMainArgs(Array.Empty<string>()), new CefSettings(), null!, IntPtr.Zero));

                Assert.AreEqual("CEF runtime cannot be initialized after shutdown.", exception!.Message);
            }
            finally
            {
                shutdownField.SetValue(null, originalValue);
            }
        }

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
