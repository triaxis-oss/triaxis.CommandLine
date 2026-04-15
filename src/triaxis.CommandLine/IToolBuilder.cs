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

    /// <summary>
    /// Parses the command-line arguments against the current command tree and returns the result.
    /// </summary>
    /// <remarks>
    /// The parse is performed at most once per builder; subsequent calls return the cached
    /// <see cref="ParseResult"/>. This lets callers inspect the parsed command line (e.g. to
    /// decide whether to build an alternate host) before committing to <see cref="IHostBuilder.Build"/>.
    /// Any further modifications to <see cref="RootCommand"/> after <see cref="Parse"/> has been
    /// called will not be reflected in the returned result.
    /// </remarks>
    ParseResult Parse();

    /// <summary>
    /// Replays this builder's configuration sources, service registrations, and deferred
    /// <see cref="IHostBuilder"/> callbacks onto another <see cref="IHostBuilder"/> so an
    /// alternate host (e.g. a <c>WebApplication</c>) can reuse the same bootstrap.
    /// </summary>
    /// <remarks>
    /// Replayed in the same order <see cref="IHostBuilder.Build"/> would apply them: direct
    /// configuration sources first, then deferred <c>ConfigureAppConfiguration</c> callbacks,
    /// then direct service descriptors (plus the current <see cref="ParseResult"/> as a
    /// singleton), then deferred <c>ConfigureServices(HostBuilderContext, IServiceCollection)</c>
    /// callbacks. Command-line specific state (the middleware chain, <c>ICommandExecutor</c>,
    /// <c>ToolHost</c>) is intentionally omitted.
    /// </remarks>
    /// <returns>The target builder, for fluent chaining on the alternate host side.</returns>
    IHostBuilder ApplyTo(IHostBuilder target);
}
