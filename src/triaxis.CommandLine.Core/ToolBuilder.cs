namespace triaxis.CommandLine;

using System;
using System.Collections.Generic;
using System.CommandLine;
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
    private readonly CommandNode _tree;
    private readonly List<CommandResultProcessor> _resultProcessors;

    public ToolBuilder(IEnumerable<string> args)
    {
        _args = args.ToArray();
        _root = new RootCommand();
        _host = Host.CreateDefaultBuilder();
        _tree = new CommandNode(_root);
        _resultProcessors = new();
        ConfigureHost(_host);
    }

    public string[] Arguments => _args;
    public RootCommand RootCommand => _root;

    public Command GetCommand(params string[] path) => _tree.GetCommand(path);

    private void ConfigureHost(IHostBuilder host)
    {
        host.ConfigureServices(services =>
        {
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

    public IToolBuilder AddResultProcessor(CommandResultProcessor processor)
    {
        _resultProcessors.Add(processor);
        return this;
    }

    public int Run()
    {
        return RunAsync().GetAwaiter().GetResult();
    }

    public async Task<int> RunAsync()
    {
        _tree.Realize();

        var parseResult = _root.Parse(_args);

        // Store ParseResult for host configuration callbacks
        _host.Properties[typeof(ParseResult)] = parseResult;

        // Check if the matched command has our custom action
        var action = parseResult.CommandResult.Command.Action;
        if (action is DependencyCommandAction depAction)
        {
            // Register ParseResult in DI
            _host.ConfigureServices(services =>
            {
                services.AddSingleton(parseResult);
            });

            var host = _host.Build();
            await host.StartAsync();

            try
            {
                var executor = host.Services.GetRequiredService<ICommandExecutor>();
                var result = await executor.ExecuteCommandAsync(depAction.CommandType);

                // Run result processors
                using var cts = new CancellationTokenSource();
                var ct = cts.Token;
                foreach (var processor in _resultProcessors)
                {
                    await processor(host.Services, parseResult, result, ct);
                }

                // Finalize the result
                if (result is ICommandInvocationResult cir)
                {
                    await cir.EnsureCompleteAsync(ct);
                }

                return 0;
            }
            catch (Exception e)
            {
                var executor = host.Services.GetRequiredService<ICommandExecutor>();
                if (!executor.HandleError(parseResult, e))
                {
                    throw;
                }
                return -1;
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
        else
        {
            // Built-in action (help, version, parse error) - let System.CommandLine handle it
            return parseResult.Invoke();
        }
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
