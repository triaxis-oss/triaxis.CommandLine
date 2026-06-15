namespace triaxis.CommandLine;

/// <summary>
/// Marker for command actions that opt out of the CLI-side service provider and
/// middleware pipeline. When the matched command's action implements this interface,
/// <c>ToolBuilder.Build()</c> short-circuits before constructing a service provider
/// and returns a minimal <c>IHost</c> that invokes the action through System.CommandLine.
/// </summary>
/// <remarks>
/// Emitted by the source generator for <c>[Command]</c> classes that declare a
/// <c>MainAsync</c> method instead of <c>ExecuteAsync</c>/<c>Execute</c>. Consumers
/// should not implement this interface manually.
/// </remarks>
public interface IStandaloneAction
{
    /// <summary>
    /// The originating <see cref="IToolBuilder"/>, stashed by <see cref="StandaloneHost"/>
    /// before the action is invoked so the command can replay its configuration/service
    /// state onto an alternate host via <see cref="IToolBuilder.ApplyTo"/>.
    /// <see langword="null"/> when the action is reached through the raw System.CommandLine
    /// pipeline (no builder available), in which case builder-taking entry points throw.
    /// </summary>
    IToolBuilder? Builder { get; set; }

    /// <summary>
    /// Whether the command's entry point declares a <see cref="CancellationToken"/> parameter.
    /// When <see langword="true"/>, <see cref="StandaloneHost"/> dispatches through
    /// <c>ParseResult.InvokeAsync</c> so System.CommandLine's process-termination handling
    /// supplies a Ctrl+C / SIGTERM token; when <see langword="false"/>, the host invokes the
    /// action directly, leaving the command in sole control of its lifecycle.
    /// </summary>
    bool ObservesCancellation { get; }
}
