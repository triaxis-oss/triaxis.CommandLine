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

    /// <summary>
    /// Registers an <see cref="ExceptionMapper"/> that turns an exception escaping a
    /// command into a clean exit (a logged error and an exit code) instead of letting
    /// it surface as an unhandled crash. Mappers are consulted in registration order;
    /// <see cref="CommandErrorException"/> is handled by a built-in fallback.
    /// </summary>
    IToolBuilder MapException(ExceptionMapper mapper);

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
    /// The replay is isolated: the tool's configuration is built into a standalone
    /// <see cref="IConfigurationRoot"/> and its services into a standalone
    /// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> against a
    /// scratch <see cref="HostBuilderContext"/>. The tool's contribution is then spliced
    /// onto the target as a single configuration source and a single bulk service
    /// registration, plus the current <see cref="ParseResult"/> as a singleton. Destructive
    /// operations inside the tool's deferred delegates (e.g. <c>cfg.Sources.Clear()</c>)
    /// therefore affect only the tool's scratch state and cannot reach user-added sources
    /// or services on the target. The target controls precedence by ordering its own
    /// registrations relative to the <see cref="ApplyTo"/> call. Command-line specific
    /// state (the middleware chain, <c>ICommandExecutor</c>, <c>ToolHost</c>) is
    /// intentionally omitted.
    /// </remarks>
    /// <returns>The target builder, for fluent chaining on the alternate host side.</returns>
    IHostBuilder ApplyTo(IHostBuilder target);
}
