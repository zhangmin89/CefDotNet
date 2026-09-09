using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xilium.CefGlue.Common.Shared.Helpers;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;

namespace Xilium.CefGlue.BrowserProcess.ObjectBinding
{
    internal class JavascriptToNativeDispatcherRenderSide : INativeObjectRegistry
    {
        private static readonly AtomicIdGenerator callIds = new AtomicIdGenerator();

        private readonly object _registrationSyncRoot = new object();
        private readonly object _pendingBoundPromiseSyncRoot = new object();
        private readonly Dictionary<(int BrowserIdentifier, string ObjectName), ObjectRegistrationInfo> _registeredObjects = new Dictionary<(int BrowserIdentifier, string ObjectName), ObjectRegistrationInfo>();
        private readonly Dictionary<int, int> _browserInstanceCounts = new Dictionary<int, int>();
        private readonly ConcurrentDictionary<int, PromiseHolder> _pendingCalls = new ConcurrentDictionary<int, PromiseHolder>();
        private readonly ConcurrentDictionary<(int BrowserIdentifier, string ObjectName), TaskCompletionSource<bool>> _pendingBoundQueryTasks = new ConcurrentDictionary<(int BrowserIdentifier, string ObjectName), TaskCompletionSource<bool>>();
        private readonly Dictionary<PromiseHolder, byte> _pendingBoundPromises = new Dictionary<PromiseHolder, byte>();
        
        public JavascriptToNativeDispatcherRenderSide(MessageDispatcher dispatcher)
        {
            dispatcher.RegisterMessageHandler(Messages.NativeObjectRegistrationRequest.Name, HandleNativeObjectRegistration);
            dispatcher.RegisterMessageHandler(Messages.NativeObjectUnregistrationRequest.Name, HandleNativeObjectUnregistration);
            dispatcher.RegisterMessageHandler(Messages.NativeObjectCallResult.Name, HandleNativeObjectCallResult);

            JavascriptHelper.Register(this);
        }

        private void HandleNativeObjectRegistration(MessageReceivedEventArgs args)
        {
            var message = Messages.NativeObjectRegistrationRequest.FromCefMessage(args.Message);
            var objectInfo = new ObjectRegistrationInfo(message.ObjectName, message.MethodsNames);
            var browserIdentifier = args.Browser.Identifier;
            var key = (browserIdentifier, objectInfo.Name);

            lock (_registrationSyncRoot)
            {
                if (_registeredObjects.ContainsKey(key))
                {
                    return;
                }

                _registeredObjects.Add(key, objectInfo);
                _pendingBoundQueryTasks.GetOrAdd(key, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

                // register objects in the main frame
                var frame = args.Browser.GetMainFrame();
                var context = frame?.V8Context;

                if (context == null)
                {
                    // bail-out, lets try later when context is created
                    return;
                }

                CreateNativeObjectsAndCompletePendingQueries(new[] { objectInfo }, context, browserIdentifier);
            }
        }

        private void HandleNativeObjectUnregistration(MessageReceivedEventArgs args)
        {
            var message = Messages.NativeObjectUnregistrationRequest.FromCefMessage(args.Message);
            if (!RemoveNativeObjectRegistration(args.Browser.Identifier, message.ObjectName))
            {
                return;
            }

            var frame = args.Browser.GetMainFrame(); // unregister objects from the main frame
            var v8Context = frame?.V8Context;
            if (v8Context == null)
            {
                return;
            }

            using (var context = v8Context.EnterOrFail())
            {
                DeleteNativeObjectValue(message.ObjectName, context.V8Context);
            }
        }

        private PromiseHolder HandleNativeObjectCall(Messages.NativeObjectCallRequest message)
        {
            return HandleNativeObjectCall(message, CefV8Context.GetCurrentContext());
        }

        private PromiseHolder HandleNativeObjectCall(Messages.NativeObjectCallRequest message, CefV8Context v8Context)
        {
            message.CallId = callIds.GetNext();
            var contextRetained = false;
            try
            {
                using (var context = v8Context.EnterOrFail(shallDispose: false)) // context will be released when promise is resolved
                {
                    var frame = context.V8Context.GetFrame();
                    if (frame == null)
                    {
                        // TODO, what now?
                        return null;
                    }

                    var promiseHolder = context.V8Context.CreatePromise();
                    if (!_pendingCalls.TryAdd(message.CallId, promiseHolder))
                    {
                        promiseHolder.Dispose();
                        throw new InvalidOperationException("Call id already exists");
                    }

                    try
                    {
                        var cefMessage = message.ToCefProcessMessage();
                        frame.SendProcessMessage(CefProcessId.Browser, cefMessage);
                    }
                    catch
                    {
                        if (_pendingCalls.TryRemove(message.CallId, out var pendingCall))
                        {
                            pendingCall.Dispose();
                        }
                        throw;
                    }

                    contextRetained = true;
                    return promiseHolder;
                }
            }
            finally
            {
                if (!contextRetained)
                {
                    v8Context.Dispose();
                }
            }
        }

        private void HandleNativeObjectCallResult(MessageReceivedEventArgs args)
        {
            var message = Messages.NativeObjectCallResult.FromCefMessage(args.Message);
            if (_pendingCalls.TryRemove(message.CallId, out var promiseHolder))
            {
                using (promiseHolder)
                using (promiseHolder.Context.EnterOrFail())
                {
                    promiseHolder.ResolveOrReject((resolve, reject) =>
                    {
                        if (message.Success)
                        {
                            var value = CefV8Value.CreateString(message.ResultAsJson);
                            resolve(value);
                        }
                        else
                        {
                            var exceptionMsg = CefV8Value.CreateString(message.Exception);
                            reject(exceptionMsg);
                        }
                    });
                }
            }
        }

        public void HandleContextCreated(CefBrowser browser, CefV8Context context, bool isMain)
        { 
            if (isMain)
            {
                lock (_registrationSyncRoot)
                {
                    CreateNativeObjectsAndCompletePendingQueries(_registeredObjects.Where(entry => entry.Key.BrowserIdentifier == browser.Identifier).Select(entry => entry.Value).ToArray(), context, browser.Identifier);
                }
            }
        }

        public void HandleContextReleased(CefV8Context context)
        {
            ReleasePendingCalls(promiseHolder => promiseHolder.Context.IsSame(context));
            ReleasePendingBoundPromises(promiseHolder => promiseHolder.Context.IsSame(context));
        }

        public void HandleBrowserCreated(CefBrowser browser)
        {
            lock (_registrationSyncRoot)
            {
                _browserInstanceCounts.TryGetValue(browser.Identifier, out var count);
                _browserInstanceCounts[browser.Identifier] = count + 1;
            }
        }

        public void HandleBrowserDestroyed(CefBrowser browser)
        {
            ReleasePendingCalls(promiseHolder => promiseHolder.Browser?.IsSame(browser) == true);
            ReleasePendingBoundPromises(promiseHolder => promiseHolder.Browser?.IsSame(browser) == true);

            lock (_registrationSyncRoot)
            {
                var browserIdentifier = browser.Identifier;
                var count = _browserInstanceCounts[browserIdentifier] - 1;
                if (count > 0)
                {
                    _browserInstanceCounts[browserIdentifier] = count;
                    return;
                }

                _browserInstanceCounts.Remove(browserIdentifier);
                foreach (var key in _registeredObjects.Keys.Where(key => key.BrowserIdentifier == browserIdentifier).ToArray())
                {
                    _registeredObjects.Remove(key);
                }
                foreach (var entry in _pendingBoundQueryTasks.Where(entry => entry.Key.BrowserIdentifier == browserIdentifier).ToArray())
                {
                    if (_pendingBoundQueryTasks.TryRemove(entry.Key, out var taskSource))
                    {
                        taskSource.TrySetCanceled();
                    }
                }
            }
        }

        private void ReleasePendingCalls(Func<PromiseHolder, bool> shallRelease)
        {
            foreach (var promiseHolderEntry in _pendingCalls.ToArray())
            {
                if (shallRelease(promiseHolderEntry.Value) && _pendingCalls.TryRemove(promiseHolderEntry.Key, out var promiseHolder))
                {
                    ReleasePromiseHolder(promiseHolder);
                }
            }
        }

        private static void ReleasePromiseHolder(PromiseHolder promiseHolder)
        {
            try
            {
                promiseHolder.Context.Dispose();
            }
            finally
            {
                promiseHolder.Dispose();
            }
        }

        private void ReleasePendingBoundPromises(Func<PromiseHolder, bool> shallRelease)
        {
            lock (_pendingBoundPromiseSyncRoot)
            {
                foreach (var promiseHolder in new List<PromiseHolder>(_pendingBoundPromises.Keys))
                {
                    if (shallRelease(promiseHolder) && _pendingBoundPromises.Remove(promiseHolder))
                    {
                        ReleasePromiseHolder(promiseHolder);
                    }
                }
            }
        }

        private void SchedulePendingBoundPromiseCompletion(PromiseHolder promiseHolder, Task<bool> boundQueryTask)
        {
            lock (_pendingBoundPromiseSyncRoot)
            {
                if (!_pendingBoundPromises.ContainsKey(promiseHolder))
                {
                    return;
                }

                try
                {
                    using (var taskRunner = promiseHolder.Context.GetTaskRunner())
                    {
                        if (!taskRunner.PostTask(new ActionTask(() => CompletePendingBoundPromise(promiseHolder, boundQueryTask))))
                        {
                            _pendingBoundPromises.Remove(promiseHolder);
                            ReleasePromiseHolder(promiseHolder);
                        }
                    }
                }
                catch
                {
                    if (_pendingBoundPromises.Remove(promiseHolder))
                    {
                        ReleasePromiseHolder(promiseHolder);
                    }
                }
            }
        }

        private void CompletePendingBoundPromise(PromiseHolder promiseHolder, Task<bool> boundQueryTask)
        {
            lock (_pendingBoundPromiseSyncRoot)
            {
                if (!_pendingBoundPromises.Remove(promiseHolder))
                {
                    return;
                }
            }

            try
            {
                using (CefObjectTracker.StartTracking())
                {
                    var context = promiseHolder.Context;
                    if (!context.Enter())
                    {
                        return;
                    }

                    try
                    {
                        promiseHolder.ResolveOrReject((resolve, reject) =>
                        {
                            if (boundQueryTask.IsCanceled)
                            {
                                reject(CefV8Value.CreateString(new TaskCanceledException(boundQueryTask).Message));
                            }
                            else if (boundQueryTask.IsFaulted)
                            {
                                reject(CefV8Value.CreateString(boundQueryTask.Exception.GetBaseException().Message));
                            }
                            else
                            {
                                resolve(CefV8Value.CreateBool(boundQueryTask.Result));
                            }
                        });
                    }
                    finally
                    {
                        context.Exit();
                    }
                }
            }
            finally
            {
                ReleasePromiseHolder(promiseHolder);
            }
        }

        private bool CreateNativeObjectsAndCompletePendingQueries(IEnumerable<ObjectRegistrationInfo> objectInfos, CefV8Context context, int browserIdentifier)
        {
            try
            {
                if (!CreateNativeObjects(objectInfos, context))
                {
                    var exception = new Exception("Failed to create native object");
                    foreach (var objectInfo in objectInfos)
                    {
                        CompletePendingBoundQueryWithException(browserIdentifier, objectInfo.Name, exception);
                    }
                    return false;
                }

                foreach (var objectInfo in objectInfos)
                {
                    if (_pendingBoundQueryTasks.TryGetValue((browserIdentifier, objectInfo.Name), out var taskSource))
                    {
                        taskSource.TrySetResult(true);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                foreach (var objectInfo in objectInfos)
                {
                    CompletePendingBoundQueryWithException(browserIdentifier, objectInfo.Name, ex);
                }
                throw;
            }
        }

        private void CompletePendingBoundQueryWithException(int browserIdentifier, string objectName, Exception exception)
        {
            if (_pendingBoundQueryTasks.TryGetValue((browserIdentifier, objectName), out var taskSource))
            {
                taskSource.TrySetException(exception);
            }
        }

        private bool CreateNativeObjects(IEnumerable<ObjectRegistrationInfo> objectInfos, CefV8Context context)
        {
            if (context.Enter())
            {
                try
                {
                    var global = context.GetGlobal();
                    foreach (var objectInfo in objectInfos)
                    {
                        var handler = new V8FunctionHandler(objectInfo.Name, HandleNativeObjectCall);
                        
                        var v8Obj = CefV8Value.CreateObject();
                        foreach (var methodName in objectInfo.MethodsNames)
                        {
                            var v8Function = CefV8Value.CreateFunction(methodName, handler);
                            v8Obj.SetValue(methodName, v8Function);
                        }

                        // the interceptor object is a proxy that will trap all calls to the native object and
                        // and pass the arguments serialized as json (to the native method)
                        var interceptorObj = JavascriptHelper.CreateInterceptorObject(context, v8Obj);
                        global.SetValue(objectInfo.Name, interceptorObj);
                    }

                    return true;
                }
                finally
                {
                    context.Exit();
                }
            }
            else
            {
                // TODO
                return false;
            }
        }

        private bool RemoveNativeObjectRegistration(int browserIdentifier, string objName)
        {
            lock (_registrationSyncRoot)
            {
                var objectRemoved = _registeredObjects.Remove((browserIdentifier, objName));
                if (_pendingBoundQueryTasks.TryRemove((browserIdentifier, objName), out var taskSource))
                {
                    taskSource.TrySetResult(false);
                }

                return objectRemoved;
            }
        }

        private static void DeleteNativeObjectValue(string objName, CefV8Context context)
        {
            var global = context.GetGlobal();
            global.DeleteValue(objName);
        }

        PromiseHolder INativeObjectRegistry.Bind(string objName, CefV8Context context)
        {
            int browserIdentifier;
            using (var browser = context.GetBrowser())
            {
                browserIdentifier = browser.Identifier;
            }
            var promiseHolder = context.CreatePromise();
            lock (_pendingBoundPromiseSyncRoot)
            {
                _pendingBoundPromises.Add(promiseHolder, 0);
            }

            var boundQueryTask = _pendingBoundQueryTasks.GetOrAdd((browserIdentifier, objName), _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _ = boundQueryTask.ContinueWith(task => SchedulePendingBoundPromiseCompletion(promiseHolder, task), TaskScheduler.Default);
            return promiseHolder;
        }

        void INativeObjectRegistry.Unbind(string objName)
        {
            using (var context = CefV8Context.GetCurrentContext().EnterOrFail())
            using (var browser = context.V8Context.GetBrowser())
            {
                if (!RemoveNativeObjectRegistration(browser.Identifier, objName))
                {
                    return;
                }

                DeleteNativeObjectValue(objName, context.V8Context);
            }
        }
    }
}
