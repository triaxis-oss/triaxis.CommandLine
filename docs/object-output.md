# Object output pipeline

`triaxis.CommandLine.ObjectOutput` turns whatever a command returns into a Table, Wide,
JSON, YAML, Raw, or discarded stream. It is implemented entirely on top of the
[middleware pipeline](middleware.md) — no changes to the core binder or executor are
required.

This document walks through every stage of that pipeline, from a command's `Execute` method
down to the bytes that hit `Console.Out`.

## 1. Return value → `ICommandInvocationResult`

Every command, regardless of what it returns, ends up wrapped in an
`ICommandInvocationResult`:

```csharp
public interface ICommandInvocationResult
{
    Task EnsureCompleteAsync(CancellationToken cancellationToken);
}

public interface ICommandInvocationResult<T> : ICommandInvocationResult
{
    Task EnumerateResultsAsync(Func<T, ValueTask> processElement,
                               Func<ValueTask>? flushHint,
                               CancellationToken cancellationToken);
    bool IsCollection { get; }
}
```

The source generator picks the concrete wrapper at compile time from the declared return
type of `Execute` / `ExecuteAsync`, and emits a direct `new XxxCommandInvocationResult<T>(…)`
into the generated action. So the **declared** return type decides the wrapper, not the
runtime type of the returned object — `public IList<Forecast> Execute()` becomes
`EnumerableCommandInvocationResult<Forecast>` even if you return a `List<Forecast>`.

Concrete wrappers:

| Return shape | Wrapper | Streaming? | `IsCollection` |
| --- | --- | --- | --- |
| `void`, `Task` | `EmptyCommandInvocationResult`, `AsyncEmptyCommandInvocationResult` | n/a | n/a |
| `T`, `Task<T>` | `ValueCommandInvocationResult<T>`, `AsyncValueCommandInvocationResult<T>` | no | `false` |
| `IEnumerable<T>`, `T[]`, `List<T>` | `EnumerableCommandInvocationResult<T>` | sync, materialized | `true` |
| `IAsyncEnumerable<T>` | `AsyncEnumerableCommandInvocationResult<T>` | **yes** | `true` |
| `Task<IEnumerable<T>>` | `AsyncIEnumerableCommandInvocationResult<T>` | after await | `true` |
| `ICommandInvocationResult` / `Task<ICommandInvocationResult>` | passed through as-is | depends on wrapper | depends |
| `System.Data.DataTable` | `ValueCommandInvocationResult<DataTable>`, routed to `DataTableObjectOutputHandler` | no | n/a |

`EnumerateResultsAsync` uses `Interlocked.Exchange` to null out the internal reference, so
enumerating a result twice is a no-op on the second call. That matters because both the
object output handler **and** `EnsureCompleteAsync` will iterate the result.

### `EnsureCompleteAsync`

`DefaultCommandExecutor` calls this after the middleware chain returns:

```csharp
if (context.InvocationResult is not null)
{
    await context.InvocationResult.EnsureCompleteAsync(context.GetCancellationToken());
}
```

For most wrappers this is a no-op. For `AsyncEnumerableCommandInvocationResult<T>` it is a
"drain" — if nothing downstream has consumed the enumerable, `EnsureCompleteAsync` runs
through it to completion so any side-effects in the command body actually happen:

```csharp
public override async Task EnsureCompleteAsync(CancellationToken cancellationToken)
{
    if (Interlocked.Exchange(ref _enumerable, null) is { } enumerable)
    {
        await foreach (var _ in enumerable.WithCancellation(cancellationToken)) { }
    }
}
```

This means you can return `IAsyncEnumerable<T>` from a command whose only purpose is side
effects (`yield return` between work items) and it will still run correctly when
`--output=None` or no formatter is registered.

## 2. ObjectOutput middleware

`UseObjectOutput()` adds one middleware:

```csharp
private static async Task ObjectOutputMiddleware(InvocationContext context, Func<InvocationContext, Task> next)
{
    await next(context);

    if (context.InvocationResult is ICommandInvocationResult cir &&
        cir.GetType().GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandInvocationResult<>)) is { } tcir)
    {
        var objectType = tcir.GetGenericArguments()[0];
        if (context.Services.GetService(typeof(IObjectOutputHandler<>).MakeGenericType(objectType))
                is IObjectOutputHandler handler)
        {
            await handler.ProcessOutputAsync(cir, context.GetCancellationToken());
        }
    }
}
```

Steps:

1. Run the command body (`next`). `context.InvocationResult` is populated as a side effect.
2. Reflect the result's generic interface to find `T`.
3. Resolve `IObjectOutputHandler<T>` from DI — the default transient registration is
   `DefaultObjectOutputHandler<T>`, with a specialisation for `DataTable`.
4. Hand the result to the handler, which streams it into the configured formatter.

Because this runs **inside** the middleware chain, any middleware registered *before*
`UseObjectOutput` runs before output happens and can replace `InvocationResult` (e.g. to
filter, map, or redact rows). `UseObjectOutput` is appended by `UseDefaults` and by the
source-generated entry point (when at least one command produces output), so by default
it is near the inner end of the chain.

> **Trimming note.** The source-generated entry point (`GeneratedProgram.g.cs`) chains
> the individual helpers directly instead of calling `UseDefaults`, and it only emits
> `.UseObjectOutput()` when at least one `[Command]` class has a return type other than
> `void`, `Task`, `int`, or `Task<int>`. In projects where every command is a void/int
> return, `triaxis.CommandLine.ObjectOutput` becomes unreachable and can be dropped by
> the trimmer. Hand-written entry points that call
> `UseDefaults()` still pull in the full stack — use the generated entry point or the
> individual helpers (`UseSerilog().UseVerbosityOptions().UseDefaultConfiguration().AddCommandsFromAssembly(...)`)
> to get the trimming benefit.

If a command sets `context.InvocationResult` to a non-generic wrapper (the empty ones, or a
`DataTable` result wrapped as `ValueCommandInvocationResult<DataTable>`), the generic
interface check still picks up `ICommandInvocationResult<T>`; `T` is then `DataTable` and the
specialised `DataTableObjectOutputHandler` handles it.

## 3. `DefaultObjectOutputHandler<T>`

```csharp
public async Task ProcessOutputAsync(ICommandInvocationResult<T> cir, CancellationToken cancellationToken)
{
    IObjectFormatter<T>? formatter = null;
    TextWriter? output = null;

    try
    {
        await cir.EnumerateResultsAsync(async e =>
        {
            if (e is not null)
            {
                if (formatter is null)
                {
                    var descriptor = _descriptorProvider.GetDescriptor(e);
                    output = _outputStreamProvider.GetOutputStream();
                    formatter = await _formatterProvider.CreateFormatterAsync<T>(descriptor, output, cir.IsCollection);
                }
                await formatter.OutputElementAsync(e);
            }
        }, flushHint: async () =>
        {
            if (output is not null) await output.FlushAsync();
        }, cancellationToken);
    }
    finally
    {
        if (formatter is IAsyncDisposable d) await d.DisposeAsync();
        if (output is not null) await output.FlushAsync();
    }
}
```

Worth highlighting:

- **Lazy formatter construction** — the descriptor and formatter are only created on the
  **first non-null element**. This lets descriptor providers look at an actual instance
  (useful for tuples and other heterogeneous element types) and avoids printing an empty
  header when there's no data.
- **`flushHint`** — `AsyncEnumerableCommandInvocationResult<T>.EnumerateResultsAsync` calls
  this between items when the next `MoveNextAsync()` would actually have to wait. It gives
  streaming formatters a chance to flush partial output so the console appears live.
- **`IsCollection`** controls whether the formatter prints a single record or a multi-row
  table. It comes from the wrapper, not from the runtime type.

## 4. Descriptors and fields

Before a formatter can render anything, it needs to know *which fields* to emit and in what
order. That's the descriptor layer:

```csharp
public interface IObjectDescriptor
{
    IReadOnlyList<IObjectField> Fields { get; }
}

public interface IObjectField
{
    string Title { get; }
    string Name { get; }
    ObjectFieldVisibility Visibility { get; }
    Type Type { get; }
    string? Format { get; }
    IPropertyGetter Accessor { get; }
}

public interface IObjectDescriptorProvider<T>
{
    IObjectDescriptor GetDescriptor(T? instance);
}
```

### Field formatting

`Format` carries the format string from `[ObjectOutput(Format = "...")]` and is applied
through `IFormattable.ToString(format, InvariantCulture)`:

```csharp
public record Reading(
    [property: ObjectOutput(Format = "F1")] double Celsius,
    [property: ObjectOutput(Format = "yyyy-MM-dd")] DateTime Taken);
```

It affects the textual formats (`Table`, `Wide`) only. `Json` and `Yaml` keep the
canonical representation of the value's type, so a consumer parsing them still sees a
number as a number and a date as a date.

The invariant culture is always used, so a table does not change shape with the host
locale.

> **Breaking change in 2.6.** `IObjectField.Converter` (a `System.ComponentModel.TypeConverter`)
> was replaced by `IObjectField.Format`. Resolving a converter requires `TypeDescriptor`,
> which is annotated `RequiresUnreferencedCode` and cannot be made trim-safe or produced
> at compile time. A custom `[TypeConverter]` on a property type is therefore no longer
> consulted — use `Format`, or replace `TableObjectFormatterProvider` via
> `ConfigureServices` for anything it cannot express.

`DefaultObjectDescriptorProvider<T>` (in `triaxis.CommandLine.ObjectOutput`) builds a
descriptor by reflecting over `T`. It understands:

- **POCOs / records** — public properties become fields. The member name becomes both
  `Name` and `Title`.
- **Tuples** — `ValueTuple<A, B, ...>` flattens each element's descriptor so a
  `IEnumerable<(Forecast, Extension)>` produces one row per tuple with combined columns.
  See `examples/ObjectOutput/Weather.cs` for the canonical example.
- **`DataTable`** — routed to a dedicated descriptor (`DataTableDescriptor`) and handler
  that translates columns/rows directly.
- **Primitives and value types** — rendered as a single-column "value" row.

### `[ObjectOutput]`

```csharp
[AttributeUsage(AttributeTargets.Property)]
public class ObjectOutputAttribute : Attribute
{
    public ObjectOutputAttribute() { }
    public ObjectOutputAttribute(ObjectFieldVisibility visibility) { Visibility = visibility; }

    public string? Before { get; set; }
    public string? After { get; set; }
    public ObjectFieldVisibility? Visibility { get; set; }
}
```

Controls three things:

- **Visibility** — `Standard` (always shown), `Extended` (only with `-o Wide`), `Internal`
  (never shown by default). `TableOutputOptions.Wide` flips extended fields on; it is
  set automatically by a `ConfigureOptions<TableOutputOptions>` registered in
  `UseObjectOutput` when `--output=Wide` is selected.
- **Ordering** — `Before = nameof(...)` / `After = nameof(...)` inserts a field at a
  specific position. Handy for computed properties or extension fields on adjacent types.
- **Position in inherited hierarchies** — fields from base types flow into the descriptor
  automatically; use `Before`/`After` to nudge them around.

## 5. Formatters

A formatter owns the actual serialization. The contract is tiny:

```csharp
public interface IObjectFormatter<in T> : IAsyncDisposable
{
    ValueTask OutputElementAsync(T element);
}

public interface IObjectFormatterProvider
{
    ValueTask<IObjectFormatter<T>> CreateFormatterAsync<T>(
        IObjectDescriptor descriptor, TextWriter output, bool isCollection);
}
```

The shipped providers:

| Provider | Format | Behaviour |
| --- | --- | --- |
| `TableObjectFormatterProvider` | `Table`, `Wide` | Columnar text with borders and padding; sizes columns dynamically. Respects `TableOutputOptions.Wide` for extended fields. |
| `JsonObjectFormatterProvider` | `Json` | Compact JSON written directly by `JsonWriter`, over the same descriptor walk as YAML; an array for collections, an object for scalars. |
| `YamlObjectFormatterProvider` | `Yaml` | Block-style YAML written directly by `YamlWriter`; no third-party YAML library. See [YAML output](#8-yaml-output). |
| `RawObjectFormatterProvider` | `Raw` | Writes `element?.ToString()` per row. Useful for piping into other shell tools. |
| `DiscardObjectFormatterProvider` | `None` | Consumes elements and throws them away — still drives the enumeration so side effects run. |

The selection happens in a DI factory that reads `ParseResult`:

```csharp
services.TryAddTransient<IObjectFormatterProvider>(sp =>
{
    var fmt = sp.GetRequiredService<ParseResult>().GetValue(optOutput);
    return fmt switch
    {
        ObjectOutputFormat.Yaml => sp.GetRequiredService<YamlObjectFormatterProvider>(),
        ObjectOutputFormat.Json => sp.GetRequiredService<JsonObjectFormatterProvider>(),
        ObjectOutputFormat.Raw  => sp.GetRequiredService<RawObjectFormatterProvider>(),
        ObjectOutputFormat.None => sp.GetRequiredService<DiscardObjectFormatterProvider>(),
        _                        => sp.GetRequiredService<TableObjectFormatterProvider>(),
    };
});
```

To add your own format:

1. Implement `IObjectFormatterProvider` (and the `IObjectFormatter<T>` it returns).
2. Register it in `ConfigureServices`.
3. Either override `IObjectFormatterProvider` entirely (simplest) or add a new enum value
   and re-register the switch factory.

## 6. The `--output` option

```csharp
public static readonly Option<ObjectOutputFormat> OutputFormatOption =
    new("--output", "-o")
    {
        DefaultValueFactory = _ => ObjectOutputFormat.Table,
        Description = "Output format",
        Recursive = true,
    };
```

`Recursive = true` tells System.CommandLine to apply the option to every subcommand in the
tree automatically. You can specify `--output=Json` at any level:

```shell
mytool forecast -o Json
mytool db migrate --output=Yaml
```

The formatter provider and the `TableOutputOptions.Wide` flag are resolved lazily from
DI, so reading `--output` at any level of the command tree just works.

## 7. Manual / multi-output commands

Some commands want to emit more than one result set. Inject `IObjectOutputHandler` directly
and call it whenever you like:

```csharp
[Command("watch")]
public class WatchCommand
{
    [Inject] private readonly IObjectOutputHandler _output = null!;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var rows = await FetchAsync(ct);
            await _output.ProcessOutputAsync(rows.ToCommandInvocationResult(), ct);
            await Task.Delay(1000, ct);
        }
    }
}
```

`ToCommandInvocationResult()` lives in `CommandInvocationResultFactoryExtensions` and has
overloads for values, `IEnumerable<T>`, `IAsyncEnumerable<T>`, `Task<T>`, and
`Task<IEnumerable<T>>`. It returns a strongly typed `ICommandInvocationResult<T>` that any
`IObjectOutputHandler<T>` or the non-generic `IObjectOutputHandler` can consume.

The non-generic `IObjectOutputHandler` registered by `UseObjectOutput` is
`DynamicObjectOutputHandler`, which dispatches to the correct `IObjectOutputHandler<T>` at
runtime by looking at the result's element type — just like the middleware does.

## 8. YAML output

`-o yaml` is produced by `YamlWriter`, a block-style YAML 1.2 emitter in the ObjectOutput
package itself. There is no third-party YAML dependency.

The emitter never produces flow context except for empty containers (`{}` / `[]`), and it
writes line breaks as `\n` explicitly rather than through `TextWriter.NewLine`, so output
does not vary with the platform. Nested values are walked through the same
`IObjectDescriptor` machinery as the root object, so `[ObjectOutput(Before = …)]` ordering
applies at every depth rather than only at the top level.

```yaml
name: web-frontend
replicas: 3
ready: true
version: v1.2.3-beta
notes: "yes"
description: |
  Handles inbound HTTP.
  Routes to the API tier.
labels:
  tier: front
  env: prod
ports:
  - 80
  - 443
probes:
  - path: /healthz
    timeout: "1:30"
taints: []
```

### When a string gets quoted

A string is emitted unquoted only when `PlainScalar.IsSafe` proves it will read back as
the same string. Because the consumer is not known at emit time, that test covers the
**union** of YAML 1.1 (PyYAML, `yq`, Ruby's Psych) and YAML 1.2 core (YamlDotNet) — a
token either schema would reinterpret is quoted. So `yes`, `y`, `off`, `null`, `007`,
`1e10`, `1:30:00` (the shape `TimeSpan.ToString()` produces) and `2026-08-06` all come out
quoted, while `hello world`, `v1.2.3-beta`, `user@example.com`, `--flag`, `::1` and
`80/TCP` stay plain.

Uncertainty always resolves to quoting, so the failure mode is output that is uglier than
necessary, never a value that reads back as the wrong type. Measured against a corpus of
~17,000 adversarial and fuzzed strings, that costs about 3% of values an unnecessary pair
of quotes.

Multi-line strings become literal block scalars (`|`, `|-`, `|+` depending on trailing
newlines). Block form is declined — falling back to a double-quoted scalar — whenever it
would not round-trip byte for byte: strings containing `\r` (block scalars normalise line
breaks), lines with leading or trailing whitespace, and strings with only one content
line.

Whole-numbered `double` and `decimal` values are written with an explicit `.0` so they
resolve as floats rather than integers on the way back.

> **Breaking change in 2.6.** The YAML formatter no longer uses YamlDotNet, so registering
> `IConfigureOptions<YamlDotNet.Serialization.SerializerBuilder>` no longer affects
> output. Replace `YamlObjectFormatterProvider` via `ConfigureServices` if you need
> different YAML.

## 9. Generated descriptors and trimming

`ObjectDescriptorGenerator` emits an `IObjectDescriptor` implementation for every type a
command can output, walked transitively through nested members and collection element
types. A module initializer registers them with `ObjectDescriptorRegistry`, which
`DefaultObjectDescriptorProvider<T>` and the nested-value walk both consult first.

Generated descriptors carry no reflection: field ordering (`Before` / `After`),
visibility and `Format` are all resolved at generation time, so the emitted field array is
already in final order and the accessor is a direct property read.

```csharp
// for: public record Forecast(string City, decimal Temperature);
if (type == typeof(global::Forecast)) { return Descriptor_0.Instance; }
...
public string Name => "City";
public global::System.String Get(object target) => ((global::Forecast)target).City;
```

Tuples are generated too: each element's members are lifted onto the tuple so
`[ObjectOutput(After = ...)]` anchors can reach across elements, exactly as
`TupleObjectDescriptor` does at run time. A tuple element with no members of its own
becomes a single field named after the element — so `(string City, int Count)` yields
`City` and `Count`. The reflective descriptor instead describes the scalar's own
properties and turns that same tuple into one `Length` column, which the generated
descriptor deliberately does not reproduce.

Types whose shape is only knowable at run time are deliberately **not** generated —
interfaces, abstract types, `object`, and `DataTable`. Those fall through to the
reflective descriptor.

### Removing the reflective fallback

The fallback is on by default and nothing needs configuring. But merely *referencing* it
keeps it alive: the trimmer cannot prove the generated lookup always hits, so it preserves
the branch, warns, and ships the reflection machinery. Opt out with:

```xml
<EnableObjectOutputReflectionFallback>false</EnableObjectOutputReflectionFallback>
```

which the package's `.targets` turns into the `AppContext` switch that
`ILLink.Substitutions.xml` stubs to `false`, making the branch dead. Measured on a trimmed
`net10.0` publish:

| | fallback on (default) | fallback off |
| --- | --- | --- |
| trim warnings | 7 | 0 |
| `System.ComponentModel.TypeConverter.dll` | shipped | dropped |
| `triaxis.Reflection.PropertyAccess.dll` | shipped in full | trimmed to ~5 KB |

`triaxis.Reflection.PropertyAccess` cannot disappear completely: `IObjectField.Accessor` is
typed as its `IPropertyGetter`, so every generated descriptor keeps that interface reachable.
Only its reflection-emit machinery goes.

With it off, a type that has no generated descriptor throws `NotSupportedException` naming
the type, rather than silently producing wrong output after the trimmer has removed the
properties it would have reflected over. `DataTable` output relies on run-time shape discovery, so it is unavailable in this
mode; tuples are generated and keep working.
