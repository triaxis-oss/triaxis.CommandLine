namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Invocation;

internal class DependencyCommandAction : AsynchronousCommandLineAction
{
    public Type CommandType { get; }
    public CommandAttribute Attribute { get; }

    public DependencyCommandAction(Type commandType, CommandAttribute attribute)
    {
        CommandType = commandType;
        Attribute = attribute;
    }

    public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Actual execution is handled by ToolBuilder.RunAsync which resolves services from the host
        // This action serves as a marker to identify commands that should use dependency injection
        return Task.FromResult(0);
    }
}
