namespace triaxis.CommandLine;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

class ToolBuilder : IToolBuilder
{
    private readonly string[] _args;
    private readonly IHostBuilder _host;
    private readonly RootCommand _root;
    private readonly CommandLineBuilder _clb;
    private readonly CommandNode _tree;
    private readonly List<InvocationMiddleware> _middlewares;
    private bool _useHost = true;
    private bool _useDefaults = true;
    private bool _useCommandFinalizer = true;

    public ToolBuilder(IEnumerable<string> args)
    {
        _args = args.ToArray();
        _root = new RootCommand();
        _host = Host.CreateDefaultBuilder();
        _clb = new CommandLineBuilder(_root);
        _tree = new CommandNode(_root);
        _middlewares = new();
        ConfigureHost(_host);
    }

    public string[] Arguments => _args;
    public RootCommand RootCommand => _root;
    public CommandLineBuilder CommandLine => _clb;

    public Command GetCommand(params string[] path) => _tree.GetCommand(path);

    private void ConfigureHost(IHostBuilder host)
    {
        host.ConfigureServices(services =>
        {
            services.Configure<InvocationLifetimeOptions>(options => options.SuppressStatusMessages = true);
            services.AddTransient<ICommandExecutor, DependencyCommandExecutor>();
            services.AddSingleton<IPropertyInjector, DependencyPropertyInjector>();
        });

        host.ConfigureLogging(logging =>
        {
            logging.AddSimpleConsole(con =>
            {
                con.ColorBehavior = LoggerColorBehavior.Enabled;
                con.SingleLine = true;
                con.TimestampFormat = "[HH:mm:ss.fff] ";
                con.IncludeScopes = true;
            });
        });
    }

    public IToolBuilder AddMiddleware(InvocationMiddleware middleware)
    {
        _middlewares.Add(middleware);
        return this;
    }

    private void UseDefaults()
    {
        if (_useDefaults)
        {
            _useDefaults = false;
            _clb.UseDefaults();
        }
    }

    private void UseHost()
    {
        if (_useHost)
        {
            _useHost = false;
            _clb.UseHost(_ => _host, null);
        }
    }

    private void UseCommandFinalizer()
    {
        if (_useCommandFinalizer)
        {
            _useCommandFinalizer = false;
            _clb.AddMiddleware(async (context, next) =>
            {
                try
                {
                    await next(context);

                    if (context.InvocationResult is ICommandInvocationResult cir)
                    {
                        await cir.EnsureCompleteAsync(context.GetCancellationToken());
                    }
                }
                catch (Exception e)
                {
                    context.ExitCode = -1;
                    var host = context.GetHost();
                    if (!host.Services.GetRequiredService<ICommandExecutor>().HandleError(context, e))
                    {
                        throw;
                    }
                }
            });
        }
    }

    public Parser Build()
    {
        UseDefaults();
        UseHost();
        UseCommandFinalizer();
        foreach (var mw in _middlewares)
        {
            _clb.AddMiddleware(mw);
        }
        _tree.Realize();
        return _clb.Build();
    }

    #region IHostBuilder implementation

    IDictionary<object, object> IHostBuilder.Properties => _host.Properties;

    IHostBuilder IHostBuilder.ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
    {
        _host.ConfigureHostConfiguration(configureDelegate);
        return this;
    }

    IHostBuilder IHostBuilder.ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
    {
        _host.ConfigureAppConfiguration(configureDelegate);
        return this;
    }

    IHostBuilder IHostBuilder.ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
    {
        _host.ConfigureServices(configureDelegate);
        return this;
    }

    IHostBuilder IHostBuilder.UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
    {
        _host.UseServiceProviderFactory(factory);
        return this;
    }

    IHostBuilder IHostBuilder.UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
    {
        _host.UseServiceProviderFactory(factory);
        return this;
    }

    IHostBuilder IHostBuilder.ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
    {
        _host.ConfigureContainer(configureDelegate);
        return this;
    }

    IHost IHostBuilder.Build()
    {
        return _host.Build();
    }

    #endregion
}
