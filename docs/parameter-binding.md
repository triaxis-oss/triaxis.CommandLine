# Parameter binding

This document describes how `[Argument]`, `[Option]`, and `[Options]` are turned into
System.CommandLine symbols and how parsed values flow back onto the command instance.

All binding is inlined into the code emitted by
`triaxis.CommandLine.SourceGenerator`; there is no runtime reflection helper. See
[source-generator.md](source-generator.md) for the generator mechanics — this document
focuses on the semantics.

## Attributes

All three parameter attributes derive from `CommandlineAttribute`:

```csharp
public class CommandlineAttribute : Attribute
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public double Order { get; set; } = 0;
}
```

| Attribute | Adds | Purpose |
| --- | --- | --- |
| `ArgumentAttribute` | `Required` | Positional argument. |
| `OptionAttribute` | `Aliases`, `Required` | Named option (`--name`, `-n`). |
| `OptionsAttribute` | — | Marker on a property whose nested type carries further `[Argument]`/`[Option]` members. |

`Required` is tri-state: unset, explicitly `true`, explicitly `false`. When unset, the C#
`required` modifier on the member is used as a fallback — any member marked with
`System.Runtime.CompilerServices.RequiredMemberAttribute` becomes a required
argument/option.

## Member → symbol

During compilation, the generator walks every `[Command]` class and emits a dedicated
action class. Each `[Argument]` or `[Option]` member becomes a field on that action holding
a plain `System.CommandLine.Argument<T>` or `Option<T>` instance. The type parameter `T` is
the member's declared value type, unwrapped from `Nullable<>`.

### Options

```csharp
// roughly what the generator emits for [Option("--name", "-n", Description = "…")] string Name
private readonly Option<string> _Name;

// in the constructor:
_Name = new Option<string>("--name", "-n");
_Name.Description = "…";
```

Notes:

- `Name` defaults to the member name. **You supply any prefix yourself** — `[Option("--name")]`
  becomes `--name`, `[Option]` on a property called `Force` becomes the literal token
  `Force`. Aliases are passed straight through.
- `Required` becomes `_Name.Required = true;` when set explicitly or when the C# `required`
  modifier is present.

### Arguments

```csharp
// roughly what the generator emits for [Argument(Description = "source", Required = true)] string Source
private readonly Argument<string> _Source;

// in the constructor:
_Source = new Argument<string>("Source");
_Source.Description = "source";
_Source.Arity = new ArgumentArity(1, _Source.Arity.MaximumNumberOfValues);
```

Arguments are positional, so "required" is expressed by adjusting `ArgumentArity` rather
than by setting a flag. The default arity comes from System.CommandLine's own
`Argument<T>`, which is why collection types and `bool` already do the right thing. The
generator flips the minimum between 0 and 1 based on `Required` / `required`.

## Writing parsed values back

After the command instance has been resolved from DI, the generated action walks every
argument/option it owns and writes the parsed value back onto the instance:

```csharp
// inside the generated action's InvokeAsync, roughly:
var instance = ActivatorUtilities.CreateInstance<HelloCommand>(provider);

// [Inject] assignments …

if (parseResult.GetResult(_Name) is { } Name_result && !Name_result.Implicit)
{
    instance.Name = parseResult.GetValue(_Name);
}

if (parseResult.GetResult(_Source) is { } Source_result && Source_result.Tokens.Any())
{
    instance.Source = parseResult.GetValue(_Source);
}
```

Two subtleties:

1. **Only explicit values overwrite defaults.** For arguments the binder checks
   `Tokens.Any()` — if the user didn't type any tokens, the member's initializer/default
   is preserved. For options it checks `!Implicit` — System.CommandLine's default values
   are "implicit" and don't overwrite your member default.
2. **Assignments go to non-public members too.** For private fields, `init`-only
   properties, or read-only properties, the generator emits either an `UnsafeAccessor`
   helper (on TFMs that support it) or a cached `FieldInfo`/`PropertyInfo` and a
   `_Set` call. See [source-generator.md](source-generator.md#reaching-non-public-members).

## Nested option groups

`[Options]` lets you factor a set of related options into a reusable POCO:

```csharp
public class ConnectionOptions
{
    [Option("--host")] public string Host { get; set; } = "localhost";
    [Option("--port")] public int    Port { get; set; } = 5432;
    [Option("--tls")]  public bool   UseTls { get; set; }
}

[Command("connect")]
public class ConnectCommand
{
    [Options] public ConnectionOptions Connection { get; set; } = new();
    public void Execute() { /* use Connection.Host, Connection.Port, ... */ }
}
```

During generation, an `[Options]` member causes the generator to recurse into the nested
type with an extended access path. The resulting `Option<T>`/`Argument<T>` still lives
directly on the command — from System.CommandLine's point of view there is no nesting. The
generator remembers the access path and emits code that walks through the nested object
when writing the value back:

```csharp
// roughly, for Connection.Host:
if (instance.Connection is null)
{
    instance.Connection = new ConnectionOptions();
}
if (parseResult.GetResult(_Connection_Host) is { } result && !result.Implicit)
{
    instance.Connection.Host = parseResult.GetValue(_Connection_Host);
}
```

So even if you don't initialize the `[Options]` property yourself, the generator creates it
on demand — provided the type has a parameterless constructor. You can nest `[Options]` as
deeply as you like; the access path grows by one entry per level.

## Type support

The element type `T` on each `Option<T>` / `Argument<T>` is whatever System.CommandLine
supports natively out of the box, plus `Nullable<T>` (unwrapped). That covers:

- Primitives (`int`, `long`, `bool`, `double`, …)
- `string`, `Guid`, `DateTime`, `DateOnly`, `TimeSpan`, `Uri`, `FileInfo`, `DirectoryInfo`
- Enums
- Arrays and common collection types (`T[]`, `List<T>`, `IEnumerable<T>`) — these get
  multiple-value arity automatically
- Any other type System.CommandLine can convert to, which you can extend by registering a
  `TypeConverter` or using the parser's own mechanisms

Because we delegate to `Option<T>` / `Argument<T>`, any conversion or validation that works
in stock System.CommandLine works here — there's no separate binder to teach.

## Binding rules summary

| Situation | Behaviour |
| --- | --- |
| Option not specified on command line | Member keeps its initializer value. |
| Option specified without a value (e.g. `bool` flag) | `parseResult.GetValue(_Option)` — usually `true` for bools. |
| `[Option]` with no `Name` | Uses the member name verbatim. You must include the `--` prefix in `Name` yourself if you want one. |
| `[Argument]` with no tokens | Member keeps its initializer; the binder skips the write. |
| `required` C# modifier on member | Argument arity becomes `(1, max)`; option becomes `Required = true`. |
| `Required = true` on attribute | Same effect; overrides the `required` modifier. |
| `Required = false` on attribute | Argument arity becomes `(0, max)` even if the member is `required`. |
| Member is on a nested `[Options]` type | Walks/creates nested instances on every write. |
