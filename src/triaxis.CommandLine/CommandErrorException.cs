namespace triaxis.CommandLine;

public class CommandErrorException : Exception
{
    public CommandErrorException(string messageTemplate, params object?[] args)
        : base(messageTemplate)
    {
        MessageArguments = args;
    }

    public object?[] MessageArguments { get; }
}
