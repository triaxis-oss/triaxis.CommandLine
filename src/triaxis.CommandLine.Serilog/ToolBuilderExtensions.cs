namespace triaxis.CommandLine;

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.SystemConsole.Themes;

public static class ToolBuilderExtensions
{
    public static IToolBuilder UseSerilog(this IToolBuilder builder, bool useShortContext = false, Action<IConfiguration, LoggerConfiguration>? configure = null)
    {
        builder.ConfigureServices(services => services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.Services.AddSingleton<ILoggerProvider>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();

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

                configure?.Invoke(configuration, loggerConfig);

                var level = VerbosityOptions.GetEffectiveLevel(sp.GetRequiredService<ParseResult>());
                loggerConfig.MinimumLevel.Is(LevelConvert.ToSerilogLevel(level));

                return new SerilogLoggerProvider(loggerConfig.CreateLogger(), dispose: true);
            });
        }));

        return builder;
    }

    public static IToolBuilder UseVerbosityOptions(this IToolBuilder builder)
    {
        builder.RootCommand.Options.Add(VerbosityOptions.Verbosity);
        builder.RootCommand.Options.Add(VerbosityOptions.Verbose);
        builder.RootCommand.Options.Add(VerbosityOptions.Quiet);
        return builder;
    }

    private static bool IsForceColorSet(ref bool sixteen)
    {
        var forceColor = System.Environment.GetEnvironmentVariable("FORCE_COLOR");
        if (forceColor == "16") { sixteen = true; return true; }
        return forceColor is not null && (forceColor == "1" || forceColor.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
