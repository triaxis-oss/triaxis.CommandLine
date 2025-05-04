namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.SystemConsole.Themes;

public static class ToolBuilderExtensions
{
    public static IToolBuilder UseSerilog(this IToolBuilder builder, bool useShortContext = false)
    {
        var levelSwitch = new LoggingLevelSwitch();

        builder.UseSerilog((context, logger) =>
        {
            logger.ReadFrom.Configuration(context.Configuration);

            if (useShortContext)
            {
                logger.Enrich.With(new ShortContextEnricher());
            }

            if (!context.Configuration.GetSection("Serilog").Exists())
            {
                var contextProperty = useShortContext ? "ShortContext" : "SourceContext";
                bool sixteen = false;
                bool theme = IsForceColorSet(ref sixteen) || !Console.IsErrorRedirected;
                // fallback configuration
                logger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {" + contextProperty + "}: {Message:lj}{NewLine}{Exception}",
                    standardErrorFromLevel: LogEventLevel.Verbose,
                    applyThemeToRedirectedOutput: theme,
                    theme: sixteen ? AnsiConsoleTheme.Sixteen : AnsiConsoleTheme.Literate
                );
            }

            logger.MinimumLevel.ControlledBy(levelSwitch);

            context.ObserveContextProperty<LogLevel>(level =>
            {
                levelSwitch.MinimumLevel = LevelConvert.ToSerilogLevel(level);
            });
        });

        return builder;
    }

    private static bool IsForceColorSet(ref bool sixteen)
    {
        var forceColor = Environment.GetEnvironmentVariable("FORCE_COLOR");
        if (forceColor == "16") { sixteen = true; return true; }
        return forceColor is not null && (forceColor == "1" || forceColor.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
