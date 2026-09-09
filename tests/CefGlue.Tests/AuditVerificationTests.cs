using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue.Common.ObjectBinding;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;
using Xilium.CefGlue.Common.Shared.Serialization;

namespace CefGlue.Tests;

// These tests document existing defects; a pass means the recorded outcome was reproduced.
[TestFixture]
[Explicit("Manual review-report reproduction; select this fixture explicitly.")]
[Category("AuditVerification")]
public class AuditVerificationTests
{
    public class Empty { }
    public class NumericTarget { public int Echo(int number) => number; }
    public class Overloaded { public void Echo(int number) { } public void Echo(string text) { } }
    public record RecordTarget(int Number);
    public class BaseMember { public int Value { get; set; } }
    public class HiddenDifferentType : BaseMember { public new string Value { get; set; } = ""; }
    public class HiddenSameType : BaseMember { public new int Value { get; set; } }

    [Test]
    public async Task V02_PipeConstructorFailureEscapesListener()
    {
        var reservedName = "cef-audit-" + Guid.NewGuid().ToString("N");
        using var reserved = new NamedPipeServerStream(reservedName, PipeDirection.In, 1);
        using var server = new PipeServer("cef-audit-" + Guid.NewGuid().ToString("N"));
        var listener = typeof(PipeServer).GetMethod("ListenAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)listener.Invoke(server, new object[] { reservedName, CancellationToken.None })!;
        Exception? error = null;
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception exception) { error = exception; }
        TestContext.Out.WriteLine($"V02 actual listener task={task.Status}; error={error}");
        Assert.IsInstanceOf<IOException>(error);
        Assert.IsTrue(task.IsFaulted);
    }

    [TestCase("[]")]
    [TestCase("[\"not-a-number\"]")]
    public void V06_ConversionExceptionBypassesCallback(string json)
    {
        var target = new NativeObject("audit", new NumericTarget());
        var called = false;
        Exception? error = null;
        try { target.ExecuteMethod("echo", json, (_, _) => called = true); }
        catch (Exception exception) { error = exception; }
        TestContext.Out.WriteLine($"V06 input={json}; callback={called}; exception={error}");
        Assert.IsNotNull(error);
        Assert.IsFalse(called);
    }

    [Test]
    public async Task V08_SuspendedNonGenericAsyncTaskReturnsVoidTaskResult()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WithoutResult() { await gate.Task; }
        var task = WithoutResult();
        gate.SetResult();
        await task;
        var actual = GenericTaskAwaiter.GetResultFrom(task);
        TestContext.Out.WriteLine($"V08 taskType={task.GetType()}; resultType={actual.Result?.GetType()}; json={Serializer.Serialize(actual.Result)}; error={actual.Exception}");
        Assert.IsNull(actual.Exception);
        Assert.IsNotNull(actual.Result);
        Assert.AreEqual("System.Threading.Tasks.VoidTaskResult", actual.Result.GetType().FullName);
        Assert.AreEqual("{}", Serializer.Serialize(actual.Result));
        Assert.IsNull(GenericTaskAwaiter.GetResultFrom(Task.CompletedTask).Result);
    }

    [Test]
    public void V09_OverloadedMethodsFailRegistration()
    {
        var error = Assert.Throws<ArgumentException>(() => new NativeObject("audit", new Overloaded()));
        TestContext.Out.WriteLine($"V09 overload: {error}");
    }

    [Test]
    public void V09_RecordEqualsOverloadsFailRegistration()
    {
        var error = Assert.Throws<ArgumentException>(() => new NativeObject("audit", new RecordTarget(1)));
        TestContext.Out.WriteLine($"V09 record: {error}");
    }

    [Test]
    public void V28_EmptyDictionaryRoundTripThrows()
    {
        var json = Serializer.Serialize(new Dictionary<string, object>());
        var error = Assert.Throws<InvalidOperationException>(() => Deserializer.Deserialize<Dictionary<string, object>>(json));
        TestContext.Out.WriteLine($"V28 json={json}; error={error}");
    }

    [Test]
    public void V28_EmptyPocoRoundTripThrows()
    {
        var json = Serializer.Serialize(new Empty());
        var error = Assert.Throws<InvalidOperationException>(() => Deserializer.Deserialize<Empty>(json));
        TestContext.Out.WriteLine($"V28 json={json}; error={error}");
    }

    [TestCase(typeof(HashSet<int>))]
    [TestCase(typeof(SortedSet<int>))]
    [TestCase(typeof(ISet<int>))]
    [TestCase(typeof(LinkedList<int>))]
    public void V29_GenericCollectionDeserialization(Type type)
    {
        var method = typeof(Deserializer).GetMethod("Deserialize", new[] { typeof(string) })!.MakeGenericMethod(type);
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { "[1,2]" }));
        TestContext.Out.WriteLine($"V29 target={type}; exception={error!.InnerException}");
        Assert.IsInstanceOf<InvalidCastException>(error.InnerException);
    }

    [Test]
    public void V30_NullableNumberThrows()
    {
        var error = Assert.Throws<InvalidCastException>(() => Deserializer.Deserialize<int?>("12"));
        TestContext.Out.WriteLine($"V30 int? input=12; exception={error}");
        Assert.IsNull(Deserializer.Deserialize<int?>("null"));
    }

    [Test]
    public void V31_HiddenDifferentTypeMemberThrows()
    {
        var error = Assert.Throws<ArgumentException>(() => Deserializer.Deserialize<HiddenDifferentType>("{\"Value\":\"text\"}"));
        TestContext.Out.WriteLine($"V31 different-type hidden member: {error}");
    }

    [Test]
    public void V31_HiddenSameTypeIsControlCase()
    {
        var actual = Deserializer.Deserialize<HiddenSameType>("{\"Value\":12}");
        TestContext.Out.WriteLine($"V31 same-type hidden member result={actual.Value}");
        Assert.AreEqual(12, actual.Value);
    }

    [TestCase(1.1, false)]
    [TestCase(1.2, false)]
    [TestCase(1.25, true)]
    [TestCase(1.5, true)]
    public void V35_DoubleFloatComparison(double scaling, bool equal)
    {
        var saved = (float)scaling;
        TestContext.Out.WriteLine($"V35 double={scaling:R}; savedFloatAsDouble={(double)saved:R}; equal={scaling == saved}");
        Assert.AreEqual(equal, scaling == saved);
    }
}
