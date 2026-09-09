using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue.Common.Events;
using Xilium.CefGlue.Common.ObjectBinding;
using Xilium.CefGlue.Common.Shared.Serialization;

namespace CefGlue.Tests.Javascript;

[TestFixture]
public class NativeObjectOverloadTests
{
    public class ArityTarget
    {
        public string Load(string url) => "one:" + url;
        public string Load(string url, int timeout) => "two:" + url + ":" + timeout;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DifferentArgumentCountsSelectDistinctOverloads(bool json)
    {
        var target = new NativeObject("overloads", new ArityTarget());
        Assert.AreEqual(1, target.MethodsNames.Count(name => name == "load"));
        Assert.AreEqual("one:page", Call(target, "load", json, "page"));
        Assert.AreEqual("two:page:42", Call(target, "load", json, "page", 42));
    }

    public class SameArityTarget
    {
        public void Log(string value) { }
        public void Log(int value) { }
    }

    public class BaseTarget { public void Log(string value) { } }
    public class DerivedTarget : BaseTarget { public void Log(int value) { } }
    public class CamelCaseTarget
    {
        public void Load(string value) { }
        public void load(int value) { }
    }
    public record RecordTarget { public int Number { get; init; } }

    [TestCase(typeof(SameArityTarget), "log")]
    [TestCase(typeof(DerivedTarget), "log")]
    [TestCase(typeof(CamelCaseTarget), "load")]
    [TestCase(typeof(RecordTarget), "equals")]
    public void AmbiguousArgumentCountsFailDuringRegistration(Type targetType, string methodName)
    {
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, targetType.GetMethods().Where(method => method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)).Select(method => $"{method.DeclaringType}: {method}")));
        var error = Assert.Throws<ArgumentException>(() => new NativeObject("ambiguous", Activator.CreateInstance(targetType)!));
        StringAssert.Contains(targetType.FullName, error!.Message);
        StringAssert.Contains($"JavaScript method '{methodName}'", error.Message);
        StringAssert.Contains("2 overloads", error.Message);
        StringAssert.Contains("argument count 1", error.Message);
    }

    public class ParamsTarget
    {
        public string Pick(int value) => "fixed:" + value;
        public string Pick(params string[] values) => "params:" + values.Length;
        public string Collect(string prefix) => "one:" + prefix;
        public string Collect(string prefix, int value) => "two:" + prefix + ":" + value;
        public string Collect(string prefix, params int[] values) => "rest:" + prefix + ":" + string.Join(",", values);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FixedArgumentCountsTakePriorityOverTheParamsCandidate(bool json)
    {
        var target = new NativeObject("params", new ParamsTarget());
        Assert.AreEqual("fixed:42", Call(target, "pick", json, 42));
        Assert.AreEqual("params:0", Call(target, "pick", json));
        Assert.AreEqual("params:2", Call(target, "pick", json, "a", "b"));
        Assert.AreEqual("one:p", Call(target, "collect", json, "p"));
        Assert.AreEqual("two:p:42", Call(target, "collect", json, "p", 42));
        Assert.AreEqual("rest:p:1,2", Call(target, "collect", json, "p", 1, 2));
    }

    public class SameMinimumParamsTarget
    {
        public void Send(params int[] values) { }
        public void Send(params string[] values) { }
    }

    public class DifferentMinimumParamsTarget
    {
        public void Send(params int[] values) { }
        public void Send(string prefix, params int[] values) { }
    }

    [TestCase(typeof(SameMinimumParamsTarget))]
    [TestCase(typeof(DifferentMinimumParamsTarget))]
    public void OverlappingParamsCandidatesFailDuringRegistration(Type targetType)
    {
        var error = Assert.Throws<ArgumentException>(() => new NativeObject("ambiguous", Activator.CreateInstance(targetType)!));
        StringAssert.Contains("JavaScript method 'send'", error!.Message);
        StringAssert.Contains("2 params overloads", error.Message);
        StringAssert.Contains("overlapping", error.Message);
    }

    public class ZeroArgumentTarget
    {
        public string Read() => "zero";
        public string Read(int value) => "one:" + value;
    }

    [TestCase("")]
    [TestCase("[]")]
    public void ZeroArgumentOverloadAcceptsEmptyProtocolAndJsonInputs(string json)
    {
        var target = new NativeObject("zero", new ZeroArgumentTarget());
        object? result = null;
        Exception? error = null;
        var callbacks = 0;
        target.ExecuteMethod("read", json, (value, exception) => { result = value; error = exception; callbacks++; });
        Assert.AreEqual(1, callbacks);
        Assert.IsNull(error);
        Assert.AreEqual("zero", result);
    }

    public class GuardTarget
    {
        public int Calls;
        public string Pick(int value) { Calls++; return "fixed"; }
        public string Pick(params string[] values) { Calls++; return "params"; }
        public string Read() { Calls++; return "zero"; }
        public string Read(int value) { Calls++; return "one"; }
        public void Send(int value) { Calls++; }
        public void Send(int value, int other) { Calls++; }
    }

    [TestCase("pick", "[\"not-a-number\"]", false)]
    [TestCase("pick", "[\"not-a-number\"]", true)]
    [TestCase("pick", "[", false)]
    [TestCase("pick", "[", true)]
    [TestCase("pick", "{}", false)]
    [TestCase("pick", "{}", true)]
    [TestCase("read", "[] []", false)]
    [TestCase("read", "[] []", true)]
    [TestCase("send", "[]", false)]
    [TestCase("send", "[]", true)]
    [TestCase("send", "[1,2,3]", false)]
    [TestCase("send", "[1,2,3]", true)]
    public void DispatchErrorsReachTheCallbackWithoutTryingAnotherOverload(string methodName, string json, bool useInterceptor)
    {
        var target = new GuardTarget();
        var intercepted = 0;
        MethodCallHandler interceptor = action => { intercepted++; return action(); };
        var native = new NativeObject("guard", target, useInterceptor ? interceptor : null!);
        var callbacks = 0;
        Exception? error = null;
        Assert.DoesNotThrow(() => native.ExecuteMethod(methodName, json, (_, failure) => { callbacks++; error = failure; }));
        Assert.IsNotNull(error);
        Assert.AreEqual(1, callbacks);
        Assert.AreEqual(0, target.Calls);
        Assert.AreEqual(0, intercepted);
        Assert.AreEqual("fixed", Call(native, "pick", true, 42));
        Assert.AreEqual(1, target.Calls);
        Assert.AreEqual(useInterceptor ? 1 : 0, intercepted);
    }

    public class Payload
    {
        public string Text { get; set; } = "";
        public int[] Values { get; set; } = Array.Empty<int>();
    }

    public class NestedTarget
    {
        public string Read(Payload value) => value.Text + ":" + value.Values.Sum();
        public int Read(Payload value, int multiplier) => value.Values.Sum() * multiplier;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedValuesCountAsOneArgument(bool json)
    {
        var target = new NativeObject("nested", new NestedTarget());
        var value = new Payload { Text = "a,[b],\"c\"", Values = new[] { 1, 2, 3 } };
        Assert.AreEqual("a,[b],\"c\":6", Call(target, "read", json, value));
        Assert.AreEqual(42, Call(target, "read", json, value, 7));
    }

    public class DefaultValueTarget
    {
        public int Read(int value = 42) => value;
        public int Read(int value, int other) => value + other;
    }

    [Test]
    public void ClrDefaultValuesDoNotIntroduceImplicitArgumentCounts()
    {
        var target = new NativeObject("defaults", new DefaultValueTarget());
        var error = Assert.Throws<ArgumentException>(() => Call(target, "read", true));
        StringAssert.Contains("no overload accepting 0 arguments", error!.Message);
        Assert.AreEqual(42, Call(target, "read", true, 42));
        Assert.AreEqual(42, Call(target, "read", true, 40, 2));
    }

    public class AsyncTarget
    {
        public Task<int> Sum(int value) => Task.FromResult(value);
        public Task<int> Sum(int value, int other) => Task.FromResult(value + other);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SelectedAsyncOverloadsPreserveTheInterceptorAndResult(bool useInterceptor)
    {
        var intercepted = 0;
        MethodCallHandler interceptor = action => { intercepted++; return action(); };
        var target = new NativeObject("async", new AsyncTarget(), useInterceptor ? interceptor : null!);
        async Task<object?> Execute(string json)
        {
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            target.ExecuteMethod("sum", json, (result, error) =>
            {
                if (error != null) completion.SetException(error);
                else completion.SetResult(result);
            });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.AreEqual(41, await Execute("[41]"));
        Assert.AreEqual(42, await Execute("[40,2]"));
        Assert.AreEqual(useInterceptor ? 2 : 0, intercepted);
    }

    private static object? Call(NativeObject target, string name, bool json, params object[] arguments)
    {
        object? result = null;
        Exception? error = null;
        var callbacks = 0;
        void Completed(object value, Exception exception) { result = value; error = exception; callbacks++; }
        if (json) target.ExecuteMethod(name, arguments.Length == 0 ? "" : Serializer.Serialize(arguments), Completed);
        else target.ExecuteMethod(name, arguments, Completed);
        Assert.AreEqual(1, callbacks);
        if (error != null) throw error;
        return result;
    }
}
