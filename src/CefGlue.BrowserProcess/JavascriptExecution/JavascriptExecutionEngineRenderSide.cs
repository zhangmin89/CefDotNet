using System;
using System.Diagnostics;
using Xilium.CefGlue.BrowserProcess.ObjectBinding;
using Xilium.CefGlue.Common.Shared.Helpers;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;

namespace Xilium.CefGlue.BrowserProcess.JavascriptExecution
{
    internal class JavascriptExecutionEngineRenderSide
    {
        public JavascriptExecutionEngineRenderSide(MessageDispatcher dispatcher)
        {
            dispatcher.RegisterMessageHandler(Messages.JsEvaluationRequest.Name, HandleJavascriptEvaluation);
        }

        private static void HandleJavascriptEvaluation(MessageReceivedEventArgs args)
        {
            var frame = args.Frame;
            var message = Messages.JsEvaluationRequest.FromCefMessage(args.Message);
            var frameIdentifier = JavascriptExecutionTrace.IsEnabled ? frame.Identifier : 0;
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(message.TaskId, frameIdentifier, "renderer-received", "");
            }

            Messages.JsEvaluationResult response;
            try
            {
                using (var context = frame.V8Context.EnterOrFail())
                {
                    // send script to browser
                    if (JavascriptExecutionTrace.IsEnabled)
                    {
                        JavascriptExecutionTrace.Write(message.TaskId, frameIdentifier, "renderer-evaluate-start", "");
                    }
                    var evaluationStarted = JavascriptExecutionTrace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
                    var success = context.V8Context.TryEval(JavascriptHelper.WrapScriptForEvaluation(message.Script), message.Url, message.Line, out var value, out var exception);
                    if (JavascriptExecutionTrace.IsEnabled)
                    {
                        JavascriptExecutionTrace.Write(message.TaskId, frameIdentifier, "renderer-evaluate-complete", FormattableString.Invariant($"success={success} executionElapsedMs={Stopwatch.GetElapsedTime(evaluationStarted).TotalMilliseconds:F3}"));
                    }

                    response = new Messages.JsEvaluationResult()
                    {
                        TaskId = message.TaskId,
                        Success = success,
                        Exception = success ? null : exception.Message,
                        ResultAsJson = value?.GetStringValue()
                    };
                }
            }
            catch (Exception exception)
            {
                response = new Messages.JsEvaluationResult()
                {
                    TaskId = message.TaskId,
                    Success = false,
                    Exception = exception.Message
                };
            }

            var cefResponseMessage = response.ToCefProcessMessage();
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(message.TaskId, frameIdentifier, "renderer-send-start", "");
            }
            frame.SendProcessMessage(CefProcessId.Browser, cefResponseMessage);
            if (JavascriptExecutionTrace.IsEnabled)
            {
                JavascriptExecutionTrace.Write(message.TaskId, frameIdentifier, "renderer-send-returned", "");
            }
        }
    }
}
