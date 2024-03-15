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
    public static IToolBuilder UseSerilog(this IToolBuilder builder)
    {
        var levelSwitch = new LoggingLevelSwitch();

        builder.UseSerilog((context, logger) =>
        {
            logger.ReadFrom.Configuration(context.Configuration);

            if (!context.Configuration.GetSection("Serilog").Exists())
            {
                // fallback configuration
                logger.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    standardErrorFromLevel: LogEventLevel.Verbose,
                    applyThemeToRedirectedOutput: Console.IsErrorRedirected ? false : true,
                    theme: AnsiConsoleTheme.Literate
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
}
