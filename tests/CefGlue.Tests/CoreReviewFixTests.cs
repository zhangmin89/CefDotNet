using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Xilium.CefGlue.Common.Events;
using Xilium.CefGlue.Common.ObjectBinding;
using Xilium.CefGlue.Common.Shared.RendererProcessCommunication;
using Xilium.CefGlue.Common.Shared.Serialization;

namespace CefGlue.Tests;

[TestFixture]
public class CoreReviewFixTests
{
    public class Empty { }
    public class BaseMember { public int Value { get; set; } }
    public class HiddenMember : BaseMember { public new string Value { get; set; } = ""; }
    public class HiddenField : BaseMember { public new string Value = ""; }
    public class ReadOnlyHiddenMember : BaseMember { public new int Value => 42; }
    public class PrivateSetterMember { public int Value { get; private set; } }
    public class InheritedPrivateSetterMember : PrivateSetterMember { }
    public class NumericTarget
    {
        public int Calls;
        public int Echo(int value) { Calls++; return value; }
    }

    [Test]
    [Platform("Win")]
    public void PipeStartupFailureIsReportedToCaller()
    {
        var name = "cef-fix-" + Guid.NewGuid().ToString("N");
        using var reserved = new NamedPipeServerStream(name, PipeDirection.In, 1);
        Assert.Throws<IOException>(() => { using var server = new PipeServer(name); });
    }

    [TestCase("[]", false)]
    [TestCase("[]", true)]
    [TestCase("[\"not-a-number\"]", false)]
    [TestCase("[\"not-a-number\"]", true)]
    [TestCase("[", false)]
    [TestCase("[", true)]
    public void InvalidArgumentsReachResultCallback(string arguments, bool useInterceptor)
    {
        var target = new NumericTarget();
        var intercepted = false;
        MethodCallHandler interceptor = action => { intercepted = true; return action(); };
        var nativeObject = new NativeObject("test", target, useInterceptor ? interceptor : null);
        var callbacks = 0;
        Exception? failure = null;
        Assert.DoesNotThrow(() => nativeObject.ExecuteMethod("echo", arguments, (_, error) => { callbacks++; failure = error; }));
        Assert.AreEqual(1, callbacks);
        Assert.IsNotNull(failure);
        Assert.AreEqual(0, target.Calls);
        Assert.IsFalse(intercepted);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ValidArgumentsStillExecuteOnce(bool useInterceptor)
    {
        var target = new NumericTarget();
        MethodCallHandler interceptor = action => action();
        var nativeObject = new NativeObject("test", target, useInterceptor ? interceptor : null);
        var callbacks = 0;
        nativeObject.ExecuteMethod("echo", "[42]", (result, error) =>
        {
            callbacks++;
            Assert.IsNull(error);
            Assert.AreEqual(42, result);
        });
        Assert.AreEqual(1, callbacks);
        Assert.AreEqual(1, target.Calls);
    }

    [Test]
    public async Task SuspendedNonGenericTaskHasNoResult()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WithoutResult() { await gate.Task; }
        var task = WithoutResult();
        Assert.IsFalse(task.IsCompleted);
        gate.SetResult();
        await task;
        var actual = GenericTaskAwaiter.GetResultFrom(task);
        Assert.IsNull(actual.Exception);
        Assert.IsNull(actual.Result);
    }

    [Test]
    public async Task SuspendedGenericTaskRetainsResult()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> WithResult() { await gate.Task; return 42; }
        var task = WithResult();
        gate.SetResult();
        await task;
        var actual = GenericTaskAwaiter.GetResultFrom(task);
        Assert.IsNull(actual.Exception);
        Assert.AreEqual(42, actual.Result);
    }

    [Test]
    public void EmptyObjectsAndDictionariesRoundTripWithReferences()
    {
        Assert.IsNotNull(Deserializer.Deserialize<Empty>(Serializer.Serialize(new Empty())));
        Assert.IsEmpty(Deserializer.Deserialize<Dictionary<string, object>>(Serializer.Serialize(new Dictionary<string, object>())));
        var empty = new Empty();
        var copy = Deserializer.Deserialize<Empty[]>(Serializer.Serialize(new[] { empty, empty }));
        Assert.IsNotNull(copy[0]);
        Assert.AreSame(copy[0], copy[1]);
    }

    [Test]
    public void EmptyDictionaryReferencesRemainShared()
    {
        var empty = new Dictionary<string, object>();
        var copy = Deserializer.Deserialize<Dictionary<string, object>[]>(Serializer.Serialize(new[] { empty, empty }));
        Assert.IsEmpty(copy[0]);
        Assert.AreSame(copy[0], copy[1]);
    }

    [TestCase(typeof(HashSet<int>))]
    [TestCase(typeof(SortedSet<int>))]
    [TestCase(typeof(LinkedList<int>))]
    [TestCase(typeof(ISet<int>))]
    public void GenericCollectionsDeserialize(Type collectionType)
    {
        var deserialize = typeof(Deserializer).GetMethod("Deserialize", new[] { typeof(string) })!.MakeGenericMethod(collectionType);
        var result = (IEnumerable)deserialize.Invoke(null, new object[] { "[1,2]" })!;
        CollectionAssert.AreEqual(new[] { 1, 2 }, result.Cast<int>().ToArray());
    }

    [Test]
    public void DictionaryAndListAddMethodsKeepTheirArgumentShapes()
    {
        var original = new Dictionary<string, List<int>> { ["numbers"] = new() { 1, 2 } };
        var copy = Deserializer.Deserialize<Dictionary<string, List<int>>>(Serializer.Serialize(original));
        CollectionAssert.AreEqual(original["numbers"], copy["numbers"]);
        var table = Deserializer.Deserialize<Hashtable>("{\"number\":12}");
        Assert.AreEqual(12d, table["number"]);
    }

    [Test]
    public void NullableNumbersPreserveValuesAndNulls()
    {
        Assert.AreEqual(12, Deserializer.Deserialize<int?>("12"));
        Assert.AreEqual(1.25d, Deserializer.Deserialize<double?>("1.25"));
        Assert.AreEqual(12m, Deserializer.Deserialize<decimal?>("12"));
        Assert.AreEqual(255, Deserializer.Deserialize<byte?>("255"));
        Assert.IsNull(Deserializer.Deserialize<int?>("null"));
        Assert.AreEqual(12, Deserializer.Deserialize<int?>("\"12\""));
    }

    [Test]
    public void NullableNumbersStillRejectInvalidAndOutOfRangeValues()
    {
        Assert.Throws<FormatException>(() => Deserializer.Deserialize<byte?>("256"));
        Assert.Throws<FormatException>(() => Deserializer.Deserialize<int?>("\"invalid\""));
    }

    [Test]
    public void HiddenPropertyUsesMostDerivedMember()
    {
        var copy = Deserializer.Deserialize<HiddenMember>("{\"Value\":\"text\"}");
        Assert.AreEqual("text", copy.Value);
        Assert.AreEqual(0, ((BaseMember)copy).Value);
    }

    [Test]
    public void ReadOnlyHiddenPropertyDoesNotExposeBaseSetter()
    {
        var copy = Deserializer.Deserialize<ReadOnlyHiddenMember>("{\"Value\":9}");
        Assert.AreEqual(42, copy.Value);
        Assert.AreEqual(0, ((BaseMember)copy).Value);
    }

    [Test]
    public void InheritedPrivateSetterRemainsExcluded()
    {
        var copy = Deserializer.Deserialize<InheritedPrivateSetterMember>("{\"Value\":9}");
        Assert.AreEqual(0, copy.Value);
        Assert.AreEqual(9, Deserializer.Deserialize<PrivateSetterMember>("{\"Value\":9}").Value);
    }

    [Test]
    public void HiddenFieldUsesMostDerivedMember()
    {
        var copy = Deserializer.Deserialize<HiddenField>("{\"Value\":\"text\"}");
        Assert.AreEqual("text", copy.Value);
        Assert.AreEqual(0, ((BaseMember)copy).Value);
    }
}
