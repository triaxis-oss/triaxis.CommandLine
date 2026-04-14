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
`required` modifier on the member is used as a fallback (detected via
`IPropertySymbol.IsRequired` / `IFieldSymbol.IsRequired`).

## Member → symbol

During compilation, the generator walks every `[Command]` class and emits a
`CommandTreeNode` containing `ArgumentDefinition<T>` and `OptionDefinition<T>` entries.
The type parameter `T` is the member's declared value type, unwrapped from `Nullable<>`.
At runtime, `ApplyTo` creates fresh System.CommandLine `Argument<T>` / `Option<T>`
instances on the target command tree.

### Options

```csharp
// what the generator emits in the CommandTreeNode for [Option("--name", "-n", Description = "…")] string Name
new OptionDefinition<string>("--name", new[] { "-n" }) { Description = "…", Arity = ArgumentArity.ExactlyOne }
```

Notes:

- When `Name` is not set explicitly, the member name is used with a `--` prefix (or `-`
  for single-character names), after stripping leading underscores.
- `Required` becomes `Required = true` on the definition when set explicitly or when the
  C# `required` modifier is present.

### Arguments

```csharp
// what the generator emits for [Argument(Description = "source", Required = true)] string Source
new ArgumentDefinition<string>("SOURCE") { Description = "source", Arity = ArgumentArity.ExactlyOne }
```

When `Name` is not set explicitly, the member name is uppercased (after stripping leading
underscores) following CLI conventions.

Arity is always set explicitly based on the member type and `Required`:

| Type | Required | Arity |
| --- | --- | --- |
| `bool` | any | `ZeroOrOne` |
| scalar | `true` / `required` | `ExactlyOne` |
| scalar | `false` / nullable | `ZeroOrOne` |
| collection | `true` / `required` | `OneOrMore` |
| collection | `false` / default | `ZeroOrMore` (arguments) / `OneOrMore` (options) |

## Writing parsed values back

After the command instance is constructed, the generated action enumerates
`parseResult.CommandResult.Children` in a single pass and binds explicit values:

```csharp
// inside the generated action's InvokeAsync, roughly:
var instance = new HelloCommand(provider.GetRequiredService<ILogger<HelloCommand>>());
// [Inject] assignments …

foreach (var __result in parseResult.CommandResult.Children)
{
    if (__result is ArgumentResult { Tokens.Count: > 0 } __ar) switch (__ar.Argument.Name)
    {
        case "SOURCE": instance.Source = __ar.GetValueOrDefault<string>(); break;
    }
    if (__result is OptionResult { Implicit: false } __or) switch (__or.Option.Name)
    {
        case "--name": instance.Name = __or.GetValueOrDefault<string>(); break;
    }
}
```

Key points:

1. **Single pass.** The outer switch dispatches on result type (`ArgumentResult` vs
   `OptionResult`), the inner switch dispatches on name (compiler-optimizable string
   jump table).
2. **Only explicit values overwrite defaults.** Arguments check `Tokens.Count > 0`;
   options check `Implicit: false`.
3. **`required` / `init` members** declared directly on the command are set in
   the object initializer via `parseResult.GetValue<T>(name)`. Required members
   inside a nested `[Options]` type are *primed* right after the container is
   resolved — again via `parseResult.GetValue<T>(name)`, so the option's declared
   default falls through when the user doesn't provide a value. Priming is
   unconditional, which is what makes a pre-initialized container with placeholder
   values (`[Options] private readonly Foo _opts = new() { Req = null! }`) work:
   the create-if-null branch never runs, but the prime does, and its backing-field
   accessor can write through `init`-only setters.
4. **Non-public members** use `UnsafeAccessor` (net8.0+) or cached
   `FieldInfo`/`PropertyInfo` — see [source-generator.md](source-generator.md#reaching-non-public-members).

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
type with an extended access path. The resulting arguments/options still live directly on
the command — from System.CommandLine's point of view there is no nesting. The nested
options are expanded in-place at the `[Options]` member's declaration position, so they
appear between any direct options declared before and after the `[Options]` property. The
generator creates the nested object if null and binds values through it:

```csharp
// roughly, for Connection.Host:
var __opts_Connection = instance.Connection ?? (instance.Connection = new ConnectionOptions());
// then in the foreach/switch:
case "--host": __opts_Connection.Host = __or.GetValueOrDefault<string>(); break;
```

So even if you don't initialize the `[Options]` property yourself, the generator creates it
on demand — provided the type has a parameterless constructor (or an initializer with
`required` members set via `parseResult.GetValue`). You can nest `[Options]` as deeply as
you like; the access path grows by one entry per level. The `[Options]` property itself can
be `required`, `init`-only, or both.

The generator walks the full inheritance hierarchy of the `[Options]` type, so
`[Option]` and `[Argument]` members declared in base classes are included
automatically — just like they are for command classes themselves.

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
| `[Option]` with no `Name` | Strips leading underscores and adds `--` (or `-` for single-char). |
| `[Argument]` with no `Name` | Strips leading underscores and uppercases. |
| `[Argument]` with no tokens | Member keeps its initializer; the binder skips the write. |
| `required` C# modifier on member | Argument arity becomes `(1, max)`; option becomes `Required = true`. |
| `Required = true` on attribute | Same effect; overrides the `required` modifier. |
| `Required = false` on attribute | Argument arity becomes `(0, max)` even if the member is `required`. |
| Member is on a nested `[Options]` type | Walks/creates nested instances on every write. |

## Ordering

Options and arguments appear in **declaration order** by default. The `Order` property
on `CommandlineAttribute` can override this, but it applies **within the group** where
the member is declared — it cannot move a member out of its `[Options]` block.

`[Options]` blocks are expanded in-place at their declaration position:

```csharp
[Command("example")]
public class ExampleCommand
{
    [Option("--alpha")] public string Alpha { get; set; }
    [Options] public DbConfig Db { get; set; } = new();   // has --host, --port
    [Option("--beta")]  public string Beta { get; set; }
}
```

produces options in the order: `--alpha`, `--host`, `--port`, `--beta`.

An explicit `Order` on a member inside `DbConfig` reorders it relative to other `DbConfig`
members, but it stays between `--alpha` and `--beta`. For inherited members, the derived
class's members come before base class members.
