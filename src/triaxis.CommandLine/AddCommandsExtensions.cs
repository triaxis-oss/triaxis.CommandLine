namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using System.Runtime.CompilerServices;

public static partial class ToolBuilderExtensions
{
    public static IToolBuilder AddCommandsFromAssembly(this IToolBuilder builder)
        => builder.AddCommandsFromAssembly(Assembly.GetCallingAssembly());

    public static IToolBuilder AddCommandsFromAssembly(this IToolBuilder builder, Assembly assembly)
    {
        var name = assembly.GetName().Name!;

        // The registration is performed by a [ModuleInitializer] emitted by the source
        // generator. Some runtimes (notably older mono versions) don't eagerly run module
        // initializers when a module is first touched via reflection, so force it.
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

        if (!GeneratedCommandRegistration.TryGet(name, out var factory))
        {
            throw new InvalidOperationException(
                $"No generated command registration found for assembly '{name}'. " +
                $"Ensure the assembly references triaxis.CommandLine so the source generator runs.");
        }

        var tree = factory(builder.GetServiceProviderAccessor());
        tree.ApplyTo(builder.RootCommand);
        return builder;
    }

    /// <summary>
    /// Adds a recursive option to the root command in a position that keeps the
    /// help output ordered from most-specific to least-specific on every command:
    /// after any local options and other user-added recursive options, but before
    /// the System.CommandLine defaults (<see cref="HelpOption"/>, <see cref="VersionOption"/>).
    /// </summary>
    /// <remarks>
    /// System.CommandLine renders inherited recursive options on subcommands in the
    /// exact order they appear on the ancestor's <c>Options</c> list, so the root's
    /// list order is what each subcommand's help text inherits. <c>ChildSymbolList</c>
    /// has no safe reorder path (Remove/Add and the indexer setter both double-register
    /// the parent), so we insert at the correct spot up front instead of moving later.
    /// </remarks>
    public static IToolBuilder AddRecursiveOption(this IToolBuilder builder, Option option)
    {
        var options = builder.RootCommand.Options;
        var insertIndex = options.Count;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i] is HelpOption or VersionOption)
            {
                insertIndex = i;
                break;
            }
        }
        options.Insert(insertIndex, option);
        return builder;
    }
}
