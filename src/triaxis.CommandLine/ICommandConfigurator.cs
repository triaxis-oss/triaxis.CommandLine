namespace triaxis.CommandLine;

/// <summary>
/// Implemented by source-generated command actions whose <c>[Command]</c> type
/// declares a static <c>Configure</c> method. <c>ToolBuilder.Build()</c> invokes
/// <see cref="Configure"/> on the matched command after parsing and before the
/// service provider is built; consumers should not implement this manually.
/// </summary>
public interface ICommandConfigurator
{
    void Configure(IToolBuilder builder);
}
