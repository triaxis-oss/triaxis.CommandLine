namespace triaxis.CommandLine;

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

public delegate Task InvocationMiddleware(InvocationContext context, Func<InvocationContext, Task> next);

public interface IToolBuilder : IHostBuilder
{
    string[] Arguments { get; }
    RootCommand RootCommand { get; }
    IConfigurationManager Configuration { get; }
    Command GetCommand(params string[] path);
    IToolBuilder AddMiddleware(InvocationMiddleware middleware);
    IToolBuilder ConfigureServices(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection> configure);
    Func<IServiceProvider> GetServiceProviderAccessor();
}
