namespace triaxis.CommandLine;

using System.CommandLine;
using Microsoft.Extensions.Configuration;

public delegate Task InvocationMiddleware(InvocationContext context, Func<InvocationContext, Task> next);

public interface IToolBuilder
{
    string[] Arguments { get; }
    RootCommand RootCommand { get; }
    IConfigurationManager Configuration { get; }
    Command GetCommand(params string[] path);
    IToolBuilder AddMiddleware(InvocationMiddleware middleware);
    IToolBuilder ConfigureServices(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection> configure);
    Func<IServiceProvider> GetServiceProviderAccessor();

    int Run();
    Task<int> RunAsync();
}
