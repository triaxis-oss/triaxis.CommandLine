namespace triaxis.CommandLine;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

class ToolBuilder : IToolBuilder
{
    private readonly string[] _args;
    private readonly RootCommand _root;
    private readonly CommandNode _tree;
    private readonly List<InvocationMiddleware> _middlewares;
    private readonly ServiceCollection _services;
    private readonly ConfigurationManager _configuration;
    private IServiceProvider? _serviceProvider;

    public IConfigurationManager Configuration => _configuration;

    public ToolBuilder(IEnumerable<string> args)
    {
        _args = args.ToArray();
        _root = new RootCommand();
        _tree = new CommandNode(_root);
        _middlewares = new();
        _services = new ServiceCollection();
        _configuration = new ConfigurationManager();
    }

    public string[] Arguments => _args;
    public RootCommand RootCommand => _root;

    public Command GetCommand(params string[] path) => _tree.GetCommand(path);

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

    public int Run()
    {
        _tree.Realize();
        var parseResult = _root.Parse(_args);
        using var provider = BuildServiceProvider(parseResult);
        return parseResult.Invoke();
    }

    public async Task<int> RunAsync()
    {
        _tree.Realize();
        var parseResult = _root.Parse(_args);
        await using var provider = BuildServiceProvider(parseResult);
        return await parseResult.InvokeAsync();
    }

    private ServiceProvider BuildServiceProvider(ParseResult parseResult)
    {
        _services.AddSingleton(parseResult);
        _services.AddSingleton<IConfiguration>(_configuration);
        _services.AddLogging();
        _services.TryAddTransient<IPropertyInjector, DependencyPropertyInjector>();
        _services.TryAddSingleton<ICommandExecutor>(sp =>
            new DefaultCommandExecutor(_middlewares, sp.GetRequiredService<ILoggerFactory>()));

        var provider = _services.BuildServiceProvider();
        _serviceProvider = provider;
        return provider;
    }
}
