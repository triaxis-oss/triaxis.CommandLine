namespace triaxis.CommandLine;

using System.CommandLine;
using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class ToolBuilderExtensions
{
    public static IToolBuilder UseVerbosityOptions(this IToolBuilder builder)
    {
        var optVerbosity = new Option<LogLevel>("--verbosity") { DefaultValueFactory = _ => LogLevel.Information, Description = "Logging level" };
        var optVerbose = new Option<bool>("-v") { Description = "Increase verbosity, equivalent to --verbosity=Debug (-v) or --verbosity=Trace (-vv)", Arity = ArgumentArity.Zero };
        var optQuiet = new Option<bool>("-q") { Description = "Decrease verbosity, equivalent to --verbosity=Warning (-q) or --verbosity=Error (-qq)", Arity = ArgumentArity.Zero };

        builder.RootCommand.Options.Add(optVerbosity);
        builder.RootCommand.Options.Add(optVerbose);
        builder.RootCommand.Options.Add(optQuiet);

        builder.ConfigureLogging((context, logging) =>
        {
            var cmdLine = context.GetParseResult();
            var verbosity = cmdLine.GetValue(optVerbosity);
            var tokV = cmdLine.GetResult(optVerbose)?.IdentifierToken;
            var tokQ = cmdLine.GetResult(optQuiet)?.IdentifierToken;
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
}
