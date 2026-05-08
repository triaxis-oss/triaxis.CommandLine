namespace triaxis.CommandLine;

using System.CommandLine;

/// <summary>
/// Implemented by source-generated command actions whose <c>[Command]</c> type
/// declares a <c>Configure</c> method. <c>ToolBuilder.Build()</c> invokes
/// <see cref="Configure"/> on the matched command after parsing and before the
/// service provider is built; consumers should not implement this manually.
/// </summary>
/// <remarks>
/// The <see cref="ParseResult"/> is passed through so that instance <c>Configure</c>
/// methods can observe their command's bound <c>[Argument]</c> / <c>[Option]</c>
/// values — the source generator constructs the command, binds parsed values onto
/// it, and then calls the user's instance method. Static <c>Configure</c> methods
/// don't need it; the generated dispatcher simply ignores the argument.
/// </remarks>
public interface ICommandConfigurator
{
    void Configure(IToolBuilder builder, ParseResult parseResult);
}
