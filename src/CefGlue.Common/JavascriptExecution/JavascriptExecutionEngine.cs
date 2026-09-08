using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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

            public long StartedTimestamp { get; } = JavascriptExecutionTrace.IsEnabled ? Stopwatch.GetTimestamp() : 0;

            public int ResultReceived;
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

            var matched = _pendingTasks.TryRemove(message.TaskId, out var pendingTask);
            if (matched)
            {
                if (JavascriptExecutionTrace.IsEnabled)
                {
                    Volatile.Write(ref pendingTask.ResultReceived, 1);
                }
                if (message.Success)
                {
                    pendingTask.CompletionSource.SetResult(message.ResultAsJson);
                }
                else
                {
                    pendingTask.CompletionSource.SetException(new Exception(message.Exception));
                }
            }
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(message.TaskId, args.Frame.Identifier, "browser-result-received", $"success={message.Success} matchedPending={matched}");
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
                    if (JavascriptExecutionTrace.IsEnabled)
                    {
                        JavascriptExecutionTrace.Write(pendingTaskEntry.Key, frameIdentifier, "browser-context-released", "");
                    }
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
                if (JavascriptExecutionTrace.IsEnabled)
                {
                    JavascriptExecutionTrace.Write(taskId, pendingEvaluation.FrameIdentifier, "browser-send-start", $"timeoutMs={timeout?.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");
                }
                var cefMessage = message.ToCefProcessMessage();
                frame.SendProcessMessage(CefProcessId.Renderer, cefMessage);
                if (JavascriptExecutionTrace.IsEnabled)
                {
                    JavascriptExecutionTrace.Write(taskId, pendingEvaluation.FrameIdentifier, "browser-send-returned", "");
                }

                return ProcessResult<T>(pendingEvaluation, taskId, timeout);
            }
            catch
            {
                _pendingTasks.TryRemove(taskId, out var _);
                if (JavascriptExecutionTrace.IsEnabled)
                {
                    JavascriptExecutionTrace.Write(taskId, pendingEvaluation.FrameIdentifier, "browser-send-failed", "");
                }
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
                    if (JavascriptExecutionTrace.IsEnabled)
                    {
                        JavascriptExecutionTrace.Write(pendingTaskEntry.Key, pendingTask.FrameIdentifier, "browser-disposed", "");
                    }
                }
            }
        }

        private async Task<T> ProcessResult<T>(PendingEvaluation pendingEvaluation, int taskId, TimeSpan? timeout)
        {
            var task = pendingEvaluation.CompletionSource.Task;
            try
            {
                if (timeout.HasValue)
                {
                    if (JavascriptExecutionTrace.IsEnabled)
                    {
                        JavascriptExecutionTrace.Write(taskId, pendingEvaluation.FrameIdentifier, "browser-timeout-armed", FormattableString.Invariant($"timeoutMs={timeout.Value.TotalMilliseconds}"));
                    }
                    var timeoutStarted = JavascriptExecutionTrace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
                    var completedTask = await Task.WhenAny(task, Task.Delay(timeout.Value)).ConfigureAwait(false);
                    if (completedTask != task)
                    {
                        if (JavascriptExecutionTrace.IsEnabled)
                        {
                            JavascriptExecutionTrace.Write(taskId, pendingEvaluation.FrameIdentifier, "browser-timeout", FormattableString.Invariant($"timeoutMs={timeout.Value.TotalMilliseconds} timeoutElapsedMs={Stopwatch.GetElapsedTime(timeoutStarted).TotalMilliseconds:F3} evaluationElapsedMs={Stopwatch.GetElapsedTime(pendingEvaluation.StartedTimestamp).TotalMilliseconds:F3} resultReceived={Volatile.Read(ref pendingEvaluation.ResultReceived) != 0} taskStatus={task.Status}"));
                        }
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
                    if (JavascriptExecutionTrace.IsEnabled)
                    {
                        JavascriptExecutionTrace.Write(taskId, pendingEvaluation.FrameIdentifier, "browser-cancelled", "");
                    }
                    return default;
                }
                var result = Deserializer.Deserialize<T>(resultAsJson);
                if (JavascriptExecutionTrace.IsEnabled)
                {
                    JavascriptExecutionTrace.Write(taskId, pendingEvaluation.FrameIdentifier, "browser-result-consumed", FormattableString.Invariant($"evaluationElapsedMs={Stopwatch.GetElapsedTime(pendingEvaluation.StartedTimestamp).TotalMilliseconds:F3}"));
                }
                return result;
            }
            finally
            {
                _pendingTasks.TryRemove(taskId, out var _);
            }
        }
    }
}
