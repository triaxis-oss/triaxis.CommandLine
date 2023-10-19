namespace triaxis.CommandLine.ObjectOutput;

public interface IObjectOutputHandler
{
    Task ProcessOutputAsync(ICommandInvocationResult cir, CancellationToken cancellationToken);
}

public interface IObjectOutputHandler<T> : IObjectOutputHandler
{
    Task ProcessOutputAsync(ICommandInvocationResult<T> cir, CancellationToken cancellationToken);
}
