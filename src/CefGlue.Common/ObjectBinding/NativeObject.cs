using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xilium.CefGlue.Common.Events;
using Xilium.CefGlue.Common.Shared.Serialization;

namespace Xilium.CefGlue.Common.ObjectBinding
{
    internal class NativeObject
    {
        private readonly object _target;
        private readonly IDictionary<string, NativeMethod[]> _methods;
        private readonly object _methodHandlerTarget;
        private readonly NativeMethod _methodHandler;
        
        public NativeObject(string name, object target, MethodCallHandler methodHandler = null)
        {
            Name = name;
            _target = target;
            _methods = GetObjectMembers(target);

            if (methodHandler != null)
            {
                _methodHandler = new NativeMethod(methodHandler.Method);
                _methodHandlerTarget = methodHandler.Target;
            }
        }

        public string Name { get; }

        public IEnumerable<string> MethodsNames => _methods.Keys;

        public void ExecuteMethod(string methodName, object[] args, Action<object, Exception> handleResult)
        {
            InnerExecuteMethod(methodName, args, handleResult);
        }

        public void ExecuteMethod(string methodName, string argsAsJson, Action<object, Exception> handleResult)
        {
            InnerExecuteMethod(methodName, argsAsJson, handleResult);
        }

        private void InnerExecuteMethod<T>(string methodName, T args, Action<object, Exception> handleResult)
        {
            if (!_methods.TryGetValue(methodName ?? "", out var methods))
            {
                handleResult(default, new Exception($"Object does not have a {methodName} method."));
                return;
            }

            NativeMethod method;
            try
            {
                if (methods.Length == 1)
                {
                    method = methods[0];
                }
                else
                {
                    var argumentCount = typeof(T) == typeof(string) ? Deserializer.GetArrayLength((string)(object)args) : ((object[])(object)args).Length;
                    method = methods.SingleOrDefault(candidate => !candidate.HasParamArray && candidate.RequiredParameterCount == argumentCount)
                        ?? methods.SingleOrDefault(candidate => candidate.HasParamArray && argumentCount >= candidate.RequiredParameterCount)
                        ?? throw new ArgumentException($"JavaScript method '{methodName}' has no overload accepting {argumentCount} arguments.");
                }
            }
            catch (Exception exception)
            {
                handleResult(default, exception);
                return;
            }

            if (_methodHandler == null)
            {
                method.Execute(_target, args, handleResult);
                return;
            }

            Func<object> innerMethod;
            try
            {
                innerMethod = method.MakeDelegate(_target, args);
            }
            catch (Exception exception)
            {
                handleResult(default, exception);
                return;
            }
            _methodHandler.Execute(_methodHandlerTarget, innerMethod, (result, exception) =>
            {
                if (result is Task task)
                {
                    task.ContinueWith(t =>
                    {
                        var taskResult = GenericTaskAwaiter.GetResultFrom(t);
                        handleResult(taskResult.Result, taskResult.Exception);
                    });
                    return;
                }

                handleResult(result, exception);
            });
        }

        private static IDictionary<string, NativeMethod[]> GetObjectMembers(object obj)
        {
            var methods = obj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => !m.IsSpecialName);
            var result = new Dictionary<string, NativeMethod[]>();
            foreach (var group in methods.GroupBy(method => ToJavascriptMemberName(method.Name)))
            {
                var overloads = group.Select(method => new NativeMethod(method)).ToArray();
                var ambiguous = overloads.Where(method => !method.HasParamArray).GroupBy(method => method.RequiredParameterCount).FirstOrDefault(candidates => candidates.Count() > 1);
                if (ambiguous != null)
                {
                    throw new ArgumentException($"Object type '{obj.GetType().FullName}' has {ambiguous.Count()} overloads for JavaScript method '{group.Key}' with argument count {ambiguous.Key}. JavaScript binding requires distinct argument counts.");
                }
                var paramsCount = overloads.Count(method => method.HasParamArray);
                if (paramsCount > 1)
                {
                    throw new ArgumentException($"Object type '{obj.GetType().FullName}' has {paramsCount} params overloads for JavaScript method '{group.Key}' with overlapping argument counts. JavaScript binding supports only one params overload per method name.");
                }
                result.Add(group.Key, overloads);
            }
            return result;
        }

        private static string ToJavascriptMemberName(string name) =>
            name.Substring(0, 1).ToLowerInvariant() + name.Substring(1);
    }
}
