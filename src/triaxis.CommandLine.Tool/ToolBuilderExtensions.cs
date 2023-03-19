namespace triaxis.CommandLine;

using System.Reflection;

public static class ToolBuilderExtensions
{
    public static IToolBuilder UseDefaults(this IToolBuilder builder)
    {
        builder.UseSerilog();
        builder.UseVerbosityOptions();
        builder.AddCommandsFromAssembly(Assembly.GetCallingAssembly());
        return builder;
    }
}
