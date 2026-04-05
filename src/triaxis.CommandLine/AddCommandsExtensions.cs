namespace triaxis.CommandLine;

using System.Reflection;

public static partial class ToolBuilderExtensions
{
    public static IToolBuilder AddCommandsFromAssembly(this IToolBuilder builder)
        => builder.AddCommandsFromAssembly(Assembly.GetCallingAssembly());

    public static IToolBuilder AddCommandsFromAssembly(this IToolBuilder builder, Assembly assembly)
    {
        var name = assembly.GetName().Name!;
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
