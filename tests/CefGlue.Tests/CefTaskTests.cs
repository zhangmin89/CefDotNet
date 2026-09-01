using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Xilium.CefGlue;
using Xilium.CefGlue.Interop;

namespace CefGlue.Tests
{
    [TestFixture]
    public unsafe class CefTaskTests
    {
        [Test]
        public void ManagedExceptionDoesNotEscapeNativeExecuteCallback()
        {
            var task = new ThrowingTask();
            var nativeTask = task.ToNative();
            var execute = Marshal.GetDelegateForFunctionPointer<cef_task_t.execute_delegate>(nativeTask->_execute);

            execute(nativeTask);

            Assert.AreEqual(1, task.InvocationCount);

            var releaseTask = Marshal.GetDelegateForFunctionPointer<cef_task_t.release_delegate>(nativeTask->_base._release);
            releaseTask(nativeTask);
        }

        private sealed class ThrowingTask : CefTask
        {
            public int InvocationCount { get; private set; }

            protected override void Execute()
            {
                InvocationCount++;
                throw new InvalidOperationException("callback failure");
            }
        }
    }
}
