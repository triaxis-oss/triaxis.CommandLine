namespace triaxis.CommandLine;

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.SystemConsole.Themes;

public static class ToolBuilderExtensions
{
    public static IToolBuilder UseSerilog(this IToolBuilder builder, bool useShortContext = false, Action<IConfiguration, LoggerConfiguration>? configure = null)
        => builder.UseSerilog(useShortContext, configure is null ? null : (context, loggerConfig) => configure(context.Configuration, loggerConfig));

    public static IToolBuilder UseSerilog(this IToolBuilder builder, Action<HostBuilderContext, LoggerConfiguration> configure)
        => builder.UseSerilog(useShortContext: false, configure);

    public static IToolBuilder UseSerilog(this IToolBuilder builder, bool useShortContext, Action<HostBuilderContext, LoggerConfiguration>? configure)
    {
        // Capture the HostBuilderContext via IHostBuilder.ConfigureServices(HostBuilderContext, ...)
        // so the Serilog factory can close over it instead of resolving it from DI (it's a
        // build-time object that shouldn't leak into the runtime container).
        IHostBuilder hostBuilder = builder;
        hostBuilder.ConfigureServices((hostBuilderContext, services) => services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.Services.AddSingleton<ILoggerProvider>(sp =>
            {
                var configuration = hostBuilderContext.Configuration;

                var loggerConfig = new LoggerConfiguration();

                loggerConfig.ReadFrom.Configuration(configuration);

                if (useShortContext)
                {
                    loggerConfig.Enrich.With(new ShortContextEnricher());
                }

                if (!configuration.GetSection("Serilog:WriteTo").Exists())
                {
                    var contextProperty = useShortContext ? "ShortContext" : "SourceContext";
                    bool sixteen = false;
                    bool theme = IsForceColorSet(ref sixteen) || !Console.IsErrorRedirected;
                    loggerConfig.WriteTo.Console(
                        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {" + contextProperty + "}: {Message:lj}{NewLine}{Exception}",
                        standardErrorFromLevel: LogEventLevel.Verbose,
                        applyThemeToRedirectedOutput: theme,
                        theme: sixteen ? AnsiConsoleTheme.Sixteen : AnsiConsoleTheme.Literate
                    );
                }

                configure?.Invoke(hostBuilderContext, loggerConfig);

                var level = VerbosityOptions.GetEffectiveLevel(sp.GetRequiredService<ParseResult>());
                loggerConfig.MinimumLevel.Is(LevelConvert.ToSerilogLevel(level));

                return new SerilogLoggerProvider(loggerConfig.CreateLogger(), dispose: true);
            });
        }));

        return builder;
    }

    public static IToolBuilder UseVerbosityOptions(this IToolBuilder builder)
    {
        // Insert ahead of System.CommandLine's built-in --help / --version so help
        // output renders the user-configured recursive options before the defaults
        // — both on the root command and on every subcommand that inherits them.
        builder.AddRecursiveOption(VerbosityOptions.Verbosity);
        builder.AddRecursiveOption(VerbosityOptions.Verbose);
        builder.AddRecursiveOption(VerbosityOptions.Quiet);
        return builder;
    }

    private static bool IsForceColorSet(ref bool sixteen)
    {
        var forceColor = System.Environment.GetEnvironmentVariable("FORCE_COLOR");
        if (forceColor == "16") { sixteen = true; return true; }
        return forceColor is not null && (forceColor == "1" || forceColor.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
