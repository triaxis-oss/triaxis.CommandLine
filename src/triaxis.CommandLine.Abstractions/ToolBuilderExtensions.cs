namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Hosting;
using System.CommandLine.Parsing;
using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class ToolBuilderExtensions
{
    public static IToolBuilder UseVerbosityOptions(this IToolBuilder builder)
    {
        var optVerbosity = new Option<LogLevel>(new[] { "--verbosity" }, () => LogLevel.Information, "Logging level");
        var optVerbose = new Option<bool>(new[] { "-v" }, "Increase verbosity, equivalent to --verbosity=Debug (-v) or --verbosity=Trace (-vv)") { Arity = ArgumentArity.Zero };
        var optQuiet = new Option<bool>(new[] { "-q" }, "Decrease verbosity, equivalent to --verbosity=Warning (-q) or --verbosity=Error (-qq)") { Arity = ArgumentArity.Zero };

        builder.RootCommand.AddGlobalOption(optVerbosity);
        builder.RootCommand.AddGlobalOption(optVerbose);
        builder.RootCommand.AddGlobalOption(optQuiet);

        builder.ConfigureLogging((context, logging) =>
        {
            var cmdLine = context.GetInvocationContext().ParseResult;
            var verbosity = cmdLine.GetValueForOption(optVerbosity);
            var tokV = cmdLine.FindResultFor(optVerbose)?.Token;
            var tokQ = cmdLine.FindResultFor(optQuiet)?.Token;
            foreach (var token in cmdLine.Tokens)
            {
                if (token.Equals(tokV))
                {
                    verbosity--;
                }
                else if (token.Equals(tokQ))
                {
                    verbosity++;
                }
            }
            context.SetContextProperty(verbosity);
            logging.SetMinimumLevel(verbosity);
        });

        return builder;
    }

    public static int Run(this IToolBuilder builder)
    {
        return builder.Build().Invoke(builder.Arguments);
    }
}
