namespace triaxis.CommandLine;

public class CommandErrorException : Exception
{
    public CommandErrorException(string messageTemplate, params object?[] args)
        : base(messageTemplate)
    {
        MessageArguments = args;
    }

    public object?[] MessageArguments { get; }

    /// <summary>
    /// Process exit code reported when this exception is mapped to a clean exit.
    /// Defaults to <c>-1</c>.
    /// </summary>
    public int ExitCode { get; set; } = -1;
}
