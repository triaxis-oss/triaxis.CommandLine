namespace triaxis.CommandLine;

using System.CommandLine;
using Microsoft.Extensions.Hosting;

public interface IToolBuilder : IHostBuilder
{
    string[] Arguments { get; }
    RootCommand RootCommand { get; }
    Command GetCommand(params string[] path);
    IToolBuilder AddResultProcessor(CommandResultProcessor processor);

    int Run();
    Task<int> RunAsync();
}

public delegate Task CommandResultProcessor(IServiceProvider services, ParseResult parseResult, ICommandInvocationResult? result, CancellationToken cancellationToken);
