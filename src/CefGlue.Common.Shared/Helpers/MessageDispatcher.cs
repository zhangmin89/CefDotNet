using System;
using System.Collections.Concurrent;

namespace Xilium.CefGlue.Common.Shared.Helpers
{
    internal class MessageDispatcher
    {
        private readonly ConcurrentDictionary<string, Action<MessageReceivedEventArgs>> _messageHandlers = new ConcurrentDictionary<string, Action<MessageReceivedEventArgs>>();

        public void DispatchMessage(CefBrowser browser, CefFrame frame, CefProcessId sourceProcess, CefProcessMessage message)
        {
            if (_messageHandlers.TryGetValue(message.Name, out var existingHandler))
            {
                existingHandler(new MessageReceivedEventArgs(browser, frame, sourceProcess, message));
            }
        }

        public void RegisterMessageHandler(string messageName, Action<MessageReceivedEventArgs> handler)
        {
            _messageHandlers.AddOrUpdate(messageName, handler, (_, existingHandler) => existingHandler + handler);
        }
    }
}
