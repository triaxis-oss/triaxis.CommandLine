namespace triaxis.CommandLine;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

class ToolBuilder : IToolBuilder, IHostBuilder
{
    private readonly string[] _args;
    private readonly RootCommand _root;
    private readonly List<InvocationMiddleware> _middlewares;
    private readonly List<ExceptionMapper> _exceptionMappers;
    private readonly ServiceCollection _services;
    private readonly ConfigurationManager _configuration;
    private readonly Dictionary<object, object> _properties = new();
    private readonly List<Action<HostBuilderContext, IConfigurationBuilder>> _appConfigActions = [];
    private readonly List<Action<HostBuilderContext, IServiceCollection>> _hostConfigureServicesActions = [];
    private IServiceProvider? _serviceProvider;
    private ParseResult? _parseResult;

    public IConfigurationManager Configuration => _configuration;

    public ToolBuilder(IEnumerable<string> args)
    {
        _args = args.ToArray();
        _root = new RootCommand();
        _middlewares = new();
        _exceptionMappers = new();
        _services = new ServiceCollection();
        _configuration = new ConfigurationManager();
    }

    public string[] Arguments => _args;
    public RootCommand RootCommand => _root;

    public Command GetCommand(params string[] path)
    {
        Command current = _root;
        foreach (var segment in path)
        {
            var subcommands = current.Subcommands;
            var child = subcommands.FirstOrDefault(c =>
                string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                child = new Command(segment);
                var index = subcommands.Count;
                while (index > 0 &&
                       string.Compare(subcommands[index - 1].Name, segment, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    index--;
                }
                subcommands.Insert(index, child);
            }
            current = child;
        }
        return current;
    }

    IToolBuilder IToolBuilder.AddMiddleware(InvocationMiddleware middleware)
        => AddMiddleware(middleware);

    public ToolBuilder AddMiddleware(InvocationMiddleware middleware)
    {
        _middlewares.Add(middleware);
        return this;
    }

    IToolBuilder IToolBuilder.MapException(ExceptionMapper mapper)
        => MapException(mapper);

    public ToolBuilder MapException(ExceptionMapper mapper)
    {
        _exceptionMappers.Add(mapper);
        return this;
    }

    IToolBuilder IToolBuilder.ConfigureServices(Action<IServiceCollection> configure)
        => ConfigureServices(configure);

    public ToolBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        configure(_services);
        return this;
    }

    public Func<IServiceProvider> GetServiceProviderAccessor() => () => _serviceProvider!;

    public IHostBuilder ApplyTo(IHostBuilder target)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        // Make sure ParseResult exists; it is registered on the target side below.
        var parseResult = Parse();

        // Seed the build-time InvocationContext on the target's Properties so that any
        // deferred IHostBuilder callback running against the target's HostBuilderContext
        // (e.g. the Serilog factory calling ctx.GetInvocationContext()) can observe the
        // parsed command line — mirroring what Build() does for the tool's own host.
        target.Properties[HostBuilderContextExtensions.InvocationContextKey] = new InvocationContext(parseResult);

        // Replay the tool's deferred delegates in isolation. Running them against the
        // target directly lets destructive operations (e.g. cfg.Sources.Clear() inside a
        // UseXxx extension) reach into state the target owns — including user-added
        // configuration sources. Building the tool's contribution into a scratch
        // ConfigurationRoot + ServiceCollection keeps "sources" and "services" meaning
        // only the tool's own sources/services when those delegates run, and lets the
        // target compose its own state around a single merge point whose ordering it
        // controls.
        _properties[HostBuilderContextExtensions.InvocationContextKey] = new InvocationContext(parseResult);
        var scratchContext = new HostBuilderContext(_properties)
        {
            Configuration = _configuration,
        };

        var scratchCfgBuilder = new ConfigurationBuilder();
        foreach (var source in _configuration.Sources)
        {
            scratchCfgBuilder.Add(source);
        }
        foreach (var action in _appConfigActions)
        {
            action(scratchContext, scratchCfgBuilder);
        }
        IConfigurationRoot toolConfiguration = scratchCfgBuilder.Build();

        // The ConfigureServices delegates see the fully built tool configuration on the
        // scratch HostBuilderContext — matching what the regular Build() path provides.
        scratchContext.Configuration = toolConfiguration;

        var scratchServices = new ServiceCollection();
        foreach (var descriptor in _services)
        {
            scratchServices.Add(descriptor);
        }
        // Make the parsed command line visible on the alternate host for handlers that
        // want to branch on it (mirrors what ToolHost exposes).
        scratchServices.AddSingleton(parseResult);

        foreach (var action in _hostConfigureServicesActions)
        {
            action(scratchContext, scratchServices);
        }

        // Splice the tool's contribution into the target as a single configuration
        // source and a single bulk service registration. The target owns the order of
        // these relative to anything else it adds.
        target.ConfigureAppConfiguration((_, cfg) => cfg.AddConfiguration(toolConfiguration));
        target.ConfigureServices((_, services) =>
        {
            foreach (var descriptor in scratchServices)
            {
                services.Add(descriptor);
            }
        });

        return target;
    }

    public ParseResult Parse()
    {
        if (_parseResult is not null)
        {
            return _parseResult;
        }

        // Parse with invariant culture so numeric/date conversions are locale-independent
        var savedCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            _parseResult = _root.Parse(_args);
        }
        finally
        {
            CultureInfo.CurrentCulture = savedCulture;
        }
        return _parseResult;
    }

    #region IHostBuilder

    IDictionary<object, object> IHostBuilder.Properties => _properties;

    IHostBuilder IHostBuilder.ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
    {
        configureDelegate(_configuration);
        return this;
    }

    IHostBuilder IHostBuilder.ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
    {
        _appConfigActions.Add(configureDelegate);
        return this;
    }

    IHostBuilder IHostBuilder.ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
    {
        _hostConfigureServicesActions.Add(configureDelegate);
        return this;
    }

    IHostBuilder IHostBuilder.UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        => throw new NotSupportedException("Custom service provider factories are not supported by ToolBuilder.");

    IHostBuilder IHostBuilder.UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
        => throw new NotSupportedException("Custom service provider factories are not supported by ToolBuilder.");

    IHostBuilder IHostBuilder.ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
        => throw new NotSupportedException("Custom containers are not supported by ToolBuilder.");

    IHost IHostBuilder.Build()
    {
        var parseResult = Parse();

        // Run the matched command's [Command] Configure hook before any IHostBuilder
        // callbacks fire so it can register services the command depends on. Built-in
        // actions (--help / --version) are different ParseResult.Action types and skip it.
        // ParseResult is forwarded so that instance Configure methods can observe their
        // command's bound argument / option values (the source generator constructs and
        // binds the command before invoking the user method). Static Configure methods
        // ignore the argument.
        if (parseResult.Action is ICommandConfigurator configurator)
        {
            configurator.Configure(this, parseResult);
        }

        // Short-circuit for commands that own their own host: no service provider,
        // no middleware, no ToolHost — the command's MainAsync runs with access to
        // this builder so it can replay registrations onto its own host.
        //
        // Use parseResult.Action (the resolved action System.CommandLine will invoke)
        // rather than the command's own Action so built-in actions like --help / --version
        // aren't short-circuited: they keep rendering through the regular ToolHost path.
        if (parseResult.Action is IStandaloneAction standalone)
        {
            return new StandaloneHost(this, standalone, parseResult);
        }

        var hostBuilderContext = new HostBuilderContext(_properties)
        {
            Configuration = _configuration,
        };
        _properties[HostBuilderContextExtensions.InvocationContextKey] = new InvocationContext(parseResult);

        foreach (var action in _appConfigActions)
        {
            action(hostBuilderContext, _configuration);
        }

        foreach (var action in _hostConfigureServicesActions)
        {
            action(hostBuilderContext, _services);
        }

        ToolHost? host = null;
        _services.AddSingleton<IHostApplicationLifetime>(_ => host!);

        _services.AddSingleton(parseResult);
        _services.AddSingleton<IConfiguration>(_configuration);
        _services.AddLogging();
        _services.TryAddSingleton<ICommandExecutor>(sp =>
            new DefaultCommandExecutor(_middlewares, _exceptionMappers, sp.GetRequiredService<ILoggerFactory>()));

        _serviceProvider = _services.BuildServiceProvider();

        host = new ToolHost(_serviceProvider, parseResult);
        return host;
    }

    #endregion
}
