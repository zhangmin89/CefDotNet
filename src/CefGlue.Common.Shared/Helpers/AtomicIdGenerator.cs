using System.Threading;

namespace Xilium.CefGlue.Common.Shared.Helpers
{
    internal sealed class AtomicIdGenerator
    {
        private int _lastId;

        public int GetNext()
        {
            return Interlocked.Increment(ref _lastId) - 1;
        }
    }
}
