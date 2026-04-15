namespace Microsoft.Extensions.Hosting;

using triaxis.CommandLine;

/// <summary>
/// Compatibility extensions that expose the command-line <see cref="InvocationContext"/>
/// from a <see cref="HostBuilderContext"/>, matching the shape of the legacy
/// <c>System.CommandLine.Hosting</c> API.
/// </summary>
public static class HostBuilderContextExtensions
{
    internal const string InvocationContextKey = "triaxis.CommandLine.InvocationContext";

    /// <summary>
    /// Returns the <see cref="InvocationContext"/> associated with the current builder.
    /// At configuration time (inside <c>ConfigureAppConfiguration</c>/<c>ConfigureServices</c>
    /// callbacks) only <see cref="InvocationContext.ParseResult"/> is populated.
    /// </summary>
    public static InvocationContext GetInvocationContext(this HostBuilderContext context)
    {
        if (context.Properties.TryGetValue(InvocationContextKey, out var value) && value is InvocationContext ctx)
        {
            return ctx;
        }

        throw new InvalidOperationException(
            "No InvocationContext is available on this HostBuilderContext. "
            + "GetInvocationContext() can only be called from IHostBuilder callbacks invoked by "
            + "ToolBuilder, or on a host that received the InvocationContext via IToolBuilder.ApplyTo.");
    }
}
