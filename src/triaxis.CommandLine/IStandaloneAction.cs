namespace triaxis.CommandLine;

using System.CommandLine;

/// <summary>
/// Marker for command actions that opt out of the CLI-side service provider and
/// middleware pipeline. When the matched command's action implements this interface,
/// <c>ToolBuilder.Build()</c> short-circuits before constructing a service provider
/// and returns a minimal <c>IHost</c> that invokes the action directly.
/// </summary>
/// <remarks>
/// Emitted by the source generator for <c>[Command]</c> classes that declare a
/// <c>MainAsync</c> method instead of <c>ExecuteAsync</c>/<c>Execute</c>. Consumers
/// should not implement this interface manually.
/// </remarks>
public interface IStandaloneAction
{
    /// <summary>
    /// Invokes the command with access to the originating <see cref="IToolBuilder"/>
    /// so the command can replay its configuration/service state onto an alternate host
    /// via <see cref="IToolBuilder.ApplyTo"/>.
    /// </summary>
    Task<int> InvokeAsync(IToolBuilder builder, ParseResult parseResult, CancellationToken cancellationToken);
}
