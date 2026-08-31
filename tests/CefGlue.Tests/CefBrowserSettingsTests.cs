using System;
using NUnit.Framework;
using Xilium.CefGlue;

namespace CefGlue.Tests
{
    [TestFixture]
    public unsafe class CefBrowserSettingsTests
    {
        [Test]
        public void DisposeReleasesOwnedNativeSettingsAndIsIdempotent()
        {
            var settings = new CefBrowserSettings();

            Assert.AreNotEqual(IntPtr.Zero, (IntPtr)settings.ToNative());
            settings.Dispose();

            Assert.AreEqual(IntPtr.Zero, (IntPtr)settings.ToNative());
            Assert.DoesNotThrow(settings.Dispose);
        }
    }
}
