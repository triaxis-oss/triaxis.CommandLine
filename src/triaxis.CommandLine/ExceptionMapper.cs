namespace triaxis.CommandLine;

/// <summary>
/// Inspects an exception escaping a command and, if it represents a clean failure,
/// returns the <see cref="CommandError"/> describing how the process should exit.
/// Returning <see langword="null"/> lets the next mapper try; if no mapper handles
/// the exception it propagates to System.CommandLine unchanged.
/// </summary>
public delegate CommandError? ExceptionMapper(Exception exception);

/// <summary>
/// Describes a clean command failure: the process exit code plus a structured-logging
/// message template and its arguments, logged via <c>ILogger.LogError</c> without a
/// stack trace.
/// </summary>
public sealed class CommandError
{
    public CommandError(int exitCode, string messageTemplate, params object?[] messageArguments)
    {
        ExitCode = exitCode;
        MessageTemplate = messageTemplate;
        MessageArguments = messageArguments;
    }

    public int ExitCode { get; }
    public string MessageTemplate { get; }
    public object?[] MessageArguments { get; }
}
