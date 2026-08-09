# NativeAOT

Tools built on `triaxis.CommandLine` publish with `PublishAot` and run. Command discovery,
parameter binding and object descriptors are all source-generated, so nothing on the hot
path needs reflection — what remains is the reflective *fallback* in ObjectOutput, and
that is a switch away from being compiled out.

```xml
<PublishAot>true</PublishAot>
<!-- for tools that use object output: -->
<EnableObjectOutputReflectionFallback>false</EnableObjectOutputReflectionFallback>
```

## Measured

`net10.0` / `linux-x64`, `InvariantGlobalization`:

| | binary | trim/AOT warnings |
| --- | --- | --- |
| `Console.WriteLine` floor, for reference | 1.09 MiB | — |
| commands, binding, DI, middleware (`void`/`int` returns) | 3.38 MiB | none |
| + object output, records / structs / tuples | 4.31 MiB | 2 × IL2075 |

These switches take the command-only tool to **2.97 MiB** and cost nothing but stack
traces:

```xml
<OptimizationPreference>Size</OptimizationPreference>
<UseSystemResourceKeys>true</UseSystemResourceKeys>
<StackTraceSupport>false</StackTraceSupport>
<EventSourceSupport>false</EventSourceSupport>
<MetadataUpdaterSupport>false</MetadataUpdaterSupport>
<IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>
```

Most of what remains is not this library: of the command-only 3.38 MiB,
`triaxis.CommandLine` itself accounts for 13.5 KiB.
`Microsoft.Extensions.DependencyInjection` alone — hello world plus one injected service,
no triaxis — already costs 2.13 MiB, including the 137 KiB of runtime type-loader and
reflection infrastructure that `ServiceProvider` needs to constructor-inject registered
services. The whole stack adds about 3 KiB on top of that.

## Why the reflection fallback has to go

Leaving the fallback on is not fatal, but it keeps `MakeGenericType` reachable, which is
`RequiresDynamicCode`. Handler resolution then closes `IObjectOutputHandler<T>` against an
open-generic registration, and AOT cannot synthesise that instantiation for a value type:
any command returning a **struct** — and therefore any command returning a **tuple** —
fails at run time with *"Unable to create a generic service … because 'T' is a
ValueType"*. With the switch off, only the generated closed registrations remain and both
work.

The switch is also what removes the trim warnings and the reflective assemblies: measured
7 trim warnings down to 0, `System.ComponentModel.TypeConverter.dll` no longer shipped,
`triaxis.Reflection.PropertyAccess.dll` trimmed to ~5 KB. See
[Removing the reflective fallback](object-output.md#removing-the-reflective-fallback) for
what the switch actually does and how it reaches ILLink.

## What does not work

Both fail with a clear exception naming the type, rather than producing wrong output:

- `System.Data.DataTable` output — its shape exists only at run time, so no descriptor can
  be generated for it.
- A command whose output type the generator never saw — an `interface` or `object` return.

The two residual IL2075 warnings are trim analysis on the `GetInterfaces()` call that finds
a result's element type. It works correctly under AOT; removing the warning would mean
putting the element type on `ICommandInvocationResult`, a breaking change not worth it yet.

## Gotcha: `AddCommandsFromAssembly()`

The parameterless overload uses `Assembly.GetEntryAssembly()`. It used to use
`Assembly.GetCallingAssembly()`, which throws `PlatformNotSupportedException` under AOT —
the two agree for a tool registering its own commands. Pass the assembly explicitly when
registering commands from a library.
