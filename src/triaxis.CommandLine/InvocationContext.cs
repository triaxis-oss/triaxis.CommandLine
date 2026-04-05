namespace triaxis.CommandLine;

using System.CommandLine;

/// <summary>
/// Context for a single command invocation, passed through middleware and the executor.
/// </summary>
public class InvocationContext
{
    private readonly CancellationToken _cancellationToken;

    public InvocationContext(IServiceProvider services, ParseResult parseResult, CancellationToken cancellationToken, Type commandType)
    {
        Services = services;
        ParseResult = parseResult;
        _cancellationToken = cancellationToken;
        CommandType = commandType;
    }

    /// <summary>
    /// Build-time invocation context used during <c>IHostBuilder.Build()</c> — only <see cref="ParseResult"/>
    /// is available; <see cref="Services"/> and <see cref="CommandType"/> are populated later when the
    /// command actually runs.
    /// </summary>
    internal InvocationContext(ParseResult parseResult)
    {
        ParseResult = parseResult;
        Services = null!;
        CommandType = null!;
    }

    /// <summary>The DI service provider for this invocation.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The parsed command-line arguments.</summary>
    public ParseResult ParseResult { get; }

    /// <summary>Cancellation token wired to Ctrl+C / SIGTERM by System.CommandLine.</summary>
    public CancellationToken GetCancellationToken() => _cancellationToken;

    /// <summary>The command class type being executed.</summary>
    public Type CommandType { get; }

    /// <summary>
    /// The result produced by the command. Set by the generated command action after
    /// invoking the command method. Consumed by middleware (e.g. ObjectOutput) and finalized
    /// by <see cref="DefaultCommandExecutor"/> via <see cref="ICommandInvocationResult.EnsureCompleteAsync"/>.
    /// </summary>
    public ICommandInvocationResult? InvocationResult { get; set; }

    /// <summary>
    /// Exit code returned to the caller. Defaults to 0. Set by the executor
    /// on error (e.g. <see cref="CommandErrorException"/>).
    /// </summary>
    public int ExitCode { get; set; }
}
