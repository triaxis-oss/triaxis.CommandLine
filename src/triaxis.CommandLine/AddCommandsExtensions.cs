namespace triaxis.CommandLine;

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

        if (!GeneratedCommandRegistration.TryGet(name, out var registration))
        {
            throw new InvalidOperationException(
                $"No generated command registration found for assembly '{name}'. " +
                $"Ensure the assembly references triaxis.CommandLine so the source generator runs.");
        }

        registration(builder);
        return builder;
    }
}
