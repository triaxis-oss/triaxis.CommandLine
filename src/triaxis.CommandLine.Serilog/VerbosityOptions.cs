namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Logging;

public static class VerbosityOptions
{
    public static readonly Option<LogLevel> Verbosity =
        new("--verbosity") { DefaultValueFactory = _ => LogLevel.Information, Description = "Logging level", Recursive = true };

    public static readonly Option<bool> Verbose =
        new("-v") { Description = "Increase verbosity, equivalent to --verbosity=Debug (-v) or --verbosity=Trace (-vv)", Arity = ArgumentArity.Zero, Recursive = true };

    public static readonly Option<bool> Quiet =
        new("-q") { Description = "Decrease verbosity, equivalent to --verbosity=Warning (-q) or --verbosity=Error (-qq)", Arity = ArgumentArity.Zero, Recursive = true };

    public static LogLevel GetEffectiveLevel(ParseResult parseResult)
    {
        var level = parseResult.GetValue(Verbosity);
        var tokV = parseResult.GetResult(Verbose)?.IdentifierToken;
        var tokQ = parseResult.GetResult(Quiet)?.IdentifierToken;
        foreach (var token in parseResult.Tokens)
        {
            if (token.Equals(tokV))
            {
                level--;
            }
            else if (token.Equals(tokQ))
            {
                level++;
            }
        }
        return level;
    }
}
