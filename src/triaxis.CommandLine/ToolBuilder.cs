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

    IToolBuilder IToolBuilder.ConfigureServices(Action<IServiceCollection> configure)
        => ConfigureServices(configure);

    public ToolBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        configure(_services);
        return this;
    }

    public Func<IServiceProvider> GetServiceProviderAccessor() => () => _serviceProvider!;

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
            new DefaultCommandExecutor(_middlewares, sp.GetRequiredService<ILoggerFactory>()));

        _serviceProvider = _services.BuildServiceProvider();

        host = new ToolHost(_serviceProvider, parseResult);
        return host;
    }

    #endregion
}
