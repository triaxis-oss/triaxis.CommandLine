# Middleware and the command executor

Every command runs inside a middleware chain owned by `ICommandExecutor`. This document
explains how the chain is built, how the default executor behaves, and how error handling
and cancellation flow through it.

## `InvocationContext`

```csharp
public class InvocationContext
{
    public IServiceProvider Services { get; }
    public ParseResult ParseResult { get; }
    public CancellationToken GetCancellationToken();
    public Type CommandType { get; }
    public ICommandInvocationResult? InvocationResult { get; set; }
    public int ExitCode { get; set; }
}
```

Every middleware receives the same context. It lets you:

- Read global options via `ParseResult`.
- Resolve services from a live `IServiceProvider`.
- Know which command type is executing (`CommandType`) — handy for logging and metrics
  without reflecting on the result.
- Inspect the return value through `InvocationResult` **after** `next` completes.
- Set `ExitCode` to influence the process exit code.

`InvocationResult` is `null` before the command body runs and populated by the generated
command action once the `Execute` method returns — see [object-output.md](object-output.md)
for what goes in it.

> There is also an internal build-time constructor that creates a context with only
> `ParseResult` populated. That one is stashed in `HostBuilderContext.Properties` so
> configuration callbacks can read parsed arguments; it never reaches middleware. See
> [hosting.md](hosting.md).

## Registering middleware

```csharp
builder.AddMiddleware(async (context, next) =>
{
    // before
    await next(context);
    // after
});
```

`InvocationMiddleware` is a delegate — no interface, no base class:

```csharp
public delegate Task InvocationMiddleware(
    InvocationContext context,
    Func<InvocationContext, Task> next);
```

The middleware list lives on `ToolBuilder` and is handed to `DefaultCommandExecutor` during
provider build. **First registered = outermost.**

## `DefaultCommandExecutor`

```csharp
public async Task ExecuteAsync(InvocationContext context, Func<Task> command)
{
    try
    {
        Func<InvocationContext, Task> chain = _ => command();
        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var mw = _middlewares[i];
            var next = chain;
            chain = ctx => mw(ctx, next);
        }

        await chain(context);

        if (context.InvocationResult is not null)
        {
            await context.InvocationResult.EnsureCompleteAsync(context.GetCancellationToken());
        }
    }
    catch (CommandErrorException e)
    {
        context.ExitCode = -1;
        var logger = _loggerFactory.CreateLogger(context.CommandType.FullName ?? context.CommandType.Name);
        logger.LogError(e.Message, e.MessageArguments);
    }
}
```

Three observations:

1. **Chain construction is right-to-left**, so the first `AddMiddleware` you register ends
   up wrapping everything else. A request flows outer → inner → command body → inner →
   outer.
2. **`EnsureCompleteAsync` runs *outside* the middleware chain.** This matters when an
   `ICommandInvocationResult<T>` streams to the console. Middleware can inspect or replace
   `context.InvocationResult` after `next` returns, but finalization (enumeration,
   flushing) happens after every middleware has completed its `after` phase.
3. **`CommandErrorException` is caught here** — and only here. Any other exception
   propagates up to System.CommandLine, which handles it with its default rules (print and
   return non-zero).

## Writing middleware — patterns

### Timing / metrics

```csharp
builder.AddMiddleware(async (context, next) =>
{
    var sw = Stopwatch.StartNew();
    try
    {
        await next(context);
    }
    finally
    {
        context.Services
            .GetRequiredService<ILogger<Program>>()
            .LogInformation("{Command} finished in {Elapsed}", context.CommandType.Name, sw.Elapsed);
    }
});
```

### Audit logging that requires the parsed values

`InvocationContext.ParseResult` is the full parse tree, so you can read any argument or
option from middleware — even ones defined on nested subcommands:

```csharp
builder.AddMiddleware((context, next) =>
{
    var tokens = context.ParseResult.Tokens.Select(t => t.Value);
    AuditSink.Record(context.CommandType, tokens);
    return next(context);
});
```

### Mutating the result

Middleware that runs after `next` can replace `context.InvocationResult`:

```csharp
builder.AddMiddleware(async (context, next) =>
{
    await next(context);
    if (context.InvocationResult is ICommandInvocationResult<User> users)
    {
        context.InvocationResult = users.Select(Redact).ToCommandInvocationResult();
    }
});
```

This is exactly how `UseObjectOutput` picks up the return value and formats it.

### Short-circuiting

Don't call `next` and the command body never runs. Set `context.ExitCode` to return a
value:

```csharp
builder.AddMiddleware((context, next) =>
{
    if (!UserIsAuthorized(context))
    {
        context.ExitCode = 77; // EX_NOPERM
        return Task.CompletedTask;
    }
    return next(context);
});
```

## Replacing the executor

`DefaultCommandExecutor` is registered with `TryAddSingleton`, so a custom `ICommandExecutor`
registered in `ConfigureServices` wins. Implement the same interface and you can own the
error-handling policy outright:

```csharp
class TracingCommandExecutor(IEnumerable<InvocationMiddleware> middlewares, ILoggerFactory lf) : ICommandExecutor
{
    public async Task ExecuteAsync(InvocationContext context, Func<Task> command)
    {
        using var _ = Activity.Start(context.CommandType.Name);
        // … delegate to a chain you assemble yourself …
    }
}

builder.ConfigureServices(s => s.AddSingleton<ICommandExecutor, TracingCommandExecutor>());
```

Note that replacing the executor opts you out of middleware unless your implementation runs
the chain itself — middleware is wired up by `DefaultCommandExecutor`, not by `ToolBuilder`.

## Error handling

Two kinds of errors reach middleware:

| Exception type | Handled by | Behaviour |
| --- | --- | --- |
| `CommandErrorException` | `DefaultCommandExecutor` | Caught, logged via `ILogger<CommandType>`, `ExitCode = -1`. No stack trace. |
| Everything else | System.CommandLine | Propagates out of `InvokeAsync`; System.CommandLine prints and returns non-zero. |

`CommandErrorException` takes a message template and arguments so it plays well with
structured logging:

```csharp
throw new CommandErrorException("File {Path} not found (errno {Err})", path, errno);
```

The message template is passed directly to `logger.LogError`, which means `{Path}` and
`{Err}` become structured log properties.

If you want to treat any exception as a user-visible error (for example to hide stack
traces in production), add a middleware that catches and rethrows:

```csharp
builder.AddMiddleware(async (context, next) =>
{
    try { await next(context); }
    catch (IOException io) { throw new CommandErrorException("I/O error: {Message}", io.Message); }
});
```

Middleware registered **before** the ObjectOutput middleware will see exceptions thrown
during streaming (because `EnsureCompleteAsync` is called outside the chain — so a failing
`IAsyncEnumerable` escapes `await next` but not the outer try/catch). Middleware can still
observe it by wrapping `next`.

## Cancellation

System.CommandLine owns the cancellation token source. It responds to `Ctrl+C` and
`SIGTERM`, and enforces a `ProcessTerminationTimeout` (default 2 seconds) after which the
process is killed. The token flows through:

```
System.CommandLine
      └── <Command>_Action.InvokeAsync(parseResult, cancellationToken)
              └── new InvocationContext(services, parseResult, cancellationToken, commandType)
                      └── ICommandExecutor.ExecuteAsync(context, ...)
                              └── every middleware via context.GetCancellationToken()
                                      └── (generated command body)
                                              └── Execute/ExecuteAsync(cancellationToken?)
```

The subtle bit is the **FailFast registration**. The generator emits this only when the
command's `Execute`/`ExecuteAsync` does **not** accept a `CancellationToken`:

```csharp
var failFastRegistration = context.GetCancellationToken().Register(static () => Environment.FailFast(null));
try
{
    // invoke the command synchronously / without token
}
finally
{
    failFastRegistration.Dispose();
}
```

If there's no cooperative way to stop the command, a Ctrl+C terminates the process. As
soon as the command body returns, the registration is disposed — middleware and result
finalization continue to run with the original token, but without the FailFast hook.

If your command **does** accept a `CancellationToken`, FailFast is never registered and
you're in charge of honouring the token.

## Execution order recap

1. Generated `*_Action.InvokeAsync` is called by System.CommandLine.
2. `InvocationContext` is created with the live `IServiceProvider`, `ParseResult`,
   `CancellationToken`, and `CommandType`.
3. `ICommandExecutor.ExecuteAsync` builds the middleware chain.
4. Each middleware runs its "before" section in registration order.
5. Generated command body:
   1. Resolve command instance via `ActivatorUtilities.CreateInstance<T>(provider)`.
   2. Assign `[Inject]` members inline.
   3. Create `[Options]` nested instances if null.
   4. Bind arguments and options.
   5. Register FailFast if the command doesn't accept `CancellationToken`.
   6. Invoke `Execute` / `ExecuteAsync`, capture the return value.
   7. Wrap the result into an `ICommandInvocationResult` and store it in the context.
   8. Dispose the FailFast registration (if any).
6. Each middleware runs its "after" section in reverse order.
7. `DefaultCommandExecutor` calls `EnsureCompleteAsync` on `context.InvocationResult`
   (streaming object output happens here).
8. `CommandErrorException` is caught and turned into `ExitCode = -1` + log entry.
9. Generated action returns `context.ExitCode` to System.CommandLine.
