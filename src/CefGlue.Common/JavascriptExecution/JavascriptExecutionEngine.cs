using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Xilium.CefGlue.Common.Events;
using Xilium.CefGlue.Common.Helpers;
using Xilium.CefGlue.Common.Shared.Helpers;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;
using Xilium.CefGlue.Common.Shared.Serialization;

namespace Xilium.CefGlue.Common.JavascriptExecution
{
    internal class JavascriptExecutionEngine : IDisposable
    {
        private sealed class PendingEvaluation
        {
            public PendingEvaluation(long frameIdentifier)
            {
                FrameIdentifier = frameIdentifier;
                CompletionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public long FrameIdentifier { get; }

            public TaskCompletionSource<string> CompletionSource { get; }
        }

        private static readonly AtomicIdGenerator taskIds = new AtomicIdGenerator();

        private readonly ConcurrentDictionary<int, PendingEvaluation> _pendingTasks = new ConcurrentDictionary<int, PendingEvaluation>();

        public JavascriptExecutionEngine(MessageDispatcher dispatcher)
        {
            dispatcher.RegisterMessageHandler(Messages.JsEvaluationResult.Name, HandleScriptEvaluationResultMessage);
            dispatcher.RegisterMessageHandler(Messages.JsContextCreated.Name, HandleContextCreatedMessage);
            dispatcher.RegisterMessageHandler(Messages.JsContextReleased.Name, HandleContextReleasedMessage);
            dispatcher.RegisterMessageHandler(Messages.JsUncaughtException.Name, HandleUncaughtExceptionMessage);
        }

        public event Action<CefFrame> ContextCreated;
        public event Action<CefFrame> ContextReleased;
        public event Action<JavascriptUncaughtExceptionEventArgs> UncaughtException;

        private void HandleScriptEvaluationResultMessage(MessageReceivedEventArgs args)
        {
            var message = Messages.JsEvaluationResult.FromCefMessage(args.Message);

            if (_pendingTasks.TryRemove(message.TaskId, out var pendingTask))
            {
                if (message.Success)
                {
                    pendingTask.CompletionSource.SetResult(message.ResultAsJson);
                }
                else
                {
                    pendingTask.CompletionSource.SetException(new Exception(message.Exception));
                }
            }
        }

        private void HandleContextCreatedMessage(MessageReceivedEventArgs args)
        {
            ContextCreated?.Invoke(args.Frame);
        }

        private void HandleContextReleasedMessage(MessageReceivedEventArgs args)
        {
            var frameIdentifier = args.Frame.Identifier;
            foreach (var pendingTaskEntry in _pendingTasks.ToArray())
            {
                if (pendingTaskEntry.Value.FrameIdentifier == frameIdentifier && _pendingTasks.TryRemove(pendingTaskEntry.Key, out var pendingTask))
                {
                    pendingTask.CompletionSource.TrySetCanceled();
                }
            }

            ContextReleased?.Invoke(args.Frame);
        }

        private void HandleUncaughtExceptionMessage(MessageReceivedEventArgs args)
        {
            var message = Messages.JsUncaughtException.FromCefMessage(args.Message);
            var stackFrames = message.StackFrames.Select(f => new JavascriptStackFrame(f.FunctionName, f.ScriptNameOrSourceUrl, f.Column, f.LineNumber));
            UncaughtException?.Invoke(new JavascriptUncaughtExceptionEventArgs(args.Frame, message.Message, stackFrames.ToArray()));
        }

        public Task<T> Evaluate<T>(string script, string url, int line, CefFrame frame, TimeSpan? timeout = null)
        {
            var taskId = taskIds.GetNext();
            var message = new Messages.JsEvaluationRequest()
            {
                TaskId = taskId,
                Script = script,
                Url = url,
                Line = line
            };

            var pendingEvaluation = new PendingEvaluation(frame.Identifier);

            _pendingTasks.TryAdd(taskId, pendingEvaluation);

            try
            {
                var cefMessage = message.ToCefProcessMessage();
                frame.SendProcessMessage(CefProcessId.Renderer, cefMessage);

                var evaluationTask = pendingEvaluation.CompletionSource.Task;
                return ProcessResult<T>(evaluationTask, taskId, timeout);
            }
            catch
            {
                _pendingTasks.TryRemove(taskId, out var _);
                throw;
            }
        }

        public void Dispose()
        {
            ContextCreated = null;
            ContextReleased = null;
            UncaughtException = null;
            foreach (var pendingTaskEntry in _pendingTasks.ToArray())
            {
                if (_pendingTasks.TryRemove(pendingTaskEntry.Key, out var pendingTask))
                {
                    pendingTask.CompletionSource.TrySetCanceled();
                }
            }
        }

        private async Task<T> ProcessResult<T>(Task<string> task, int taskId, TimeSpan? timeout)
        {
            try
            {
                if (timeout.HasValue)
                {
                    var completedTask = await Task.WhenAny(task, Task.Delay(timeout.Value)).ConfigureAwait(false);
                    if (completedTask != task)
                    {
                        throw new TaskCanceledException();
                    }
                }

                string resultAsJson;
                try
                {
                    resultAsJson = await task.ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return default;
                }
                return Deserializer.Deserialize<T>(resultAsJson);
            }
            finally
            {
                _pendingTasks.TryRemove(taskId, out var _);
            }
        }
    }
}
