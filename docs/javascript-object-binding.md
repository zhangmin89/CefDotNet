# JavaScript object binding: method overloads

`RegisterJavascriptObject` exposes public instance methods, excluding property/event accessors. Method names use the existing first-letter lowercase convention. The four inherited `System.Object` methods (`toString`, `getHashCode`, `getType`, and `equals`) remain exposed.

Each JavaScript method name is registered once. An overloaded name uses the number of supplied arguments to select its .NET method:

1. Select a fixed-parameter overload with exactly that many parameters.
2. Otherwise, select the single `params` overload if the argument count covers every parameter before the parameter array.
3. If neither matches, reject the call through the existing error callback / JavaScript Promise.

Only the top-level argument count participates in selection. Nested arrays and objects each count as one argument. After selection, the existing deserializer uses that method's parameter types. A JSON conversion error rejects the call; it does not try another overload or invoke the interceptor.

For example:

```csharp
public class Loader
{
    public string Load(string url) => url;
    public string Load(string url, int timeout) => url + ":" + timeout;

    public string Pick(int value) => "fixed:" + value;
    public string Pick(params string[] values) => "params:" + values.Length;
}
```

```javascript
await loader.load("page");       // Load(string)
await loader.load("page", 42);   // Load(string, int)
await loader.pick(42);           // Pick(int)
await loader.pick();             // Pick(params string[]) with an empty array
await loader.pick("a", "b");     // Pick(params string[])
await loader.pick("text");       // Rejected: Pick(int) was selected; no type fallback
```

Registration throws an `ArgumentException` identifying the .NET type, JavaScript method name and conflicting count when:

- Two fixed-parameter overloads have the same argument count.
- A JavaScript name has more than one `params` overload. Their accepted count ranges overlap, even when their required parameter counts differ.

The checks apply after JavaScript name normalization and across inherited public methods. There is no priority based on declaring type, parameter type, reflection order or name suffix. A fixed overload can coexist with one `params` overload, including when their declared parameter counts are equal; the fixed overload has the priority defined above.

C# default parameter values do not add implicit arities. For example, `Read(int value = 42)` still requires one supplied argument. Use distinct fixed-parameter overloads to expose calls with different argument counts.

Records remain subject to registration ambiguity checks. A normal record class declares both `Equals(object)` and `Equals(RecordType)`; both have one argument and map to `equals`. Both methods are declared on the record itself, so a declaring-type preference alone would not resolve this conflict. This binding contract rejects that record with a specific registration error.

The wire format, JavaScript function names, Promise behavior, and existing interceptor/async result handling remain unchanged. Unambiguous methods retain their existing invocation path.

Regression coverage: [native registration and dispatch](../tests/CefGlue.Tests/Javascript/NativeObjectOverloadTests.cs), [existing Object method contract](../tests/CefGlue.Tests/Javascript/NativeObjectInstrospectionTests.cs), and [real browser Promise calls](../tests/CefGlue.Avalonia.Tests/BrowserReviewFixTests.cs).
