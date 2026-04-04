namespace triaxis.CommandLine;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

public static class ToolBuilderExtensions
{
    public static IToolBuilder UseDefaults(this IToolBuilder builder,
        string? configOverridePath = null,
        string? environmentVariablePrefix = null,
        Assembly? commandsAssembly = null)
    {
        builder.UseSerilog();
        builder.UseVerbosityOptions();
        builder.UseObjectOutput();
        builder.AddCommandsFromAssembly(commandsAssembly ?? Assembly.GetCallingAssembly());

        var config = builder.Configuration;
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", optional: true);

        if (configOverridePath is not null)
        {
            void AddOverrideConfig(Environment.SpecialFolder folder)
            {
                var path = Path.Combine(Environment.GetFolderPath(folder), configOverridePath);
                if (File.Exists(path))
                {
                    config.AddJsonFile(new PhysicalFileProvider(Path.GetDirectoryName(path)), Path.GetFileName(path), optional: false, reloadOnChange: false);
                }
            }

            AddOverrideConfig(Environment.SpecialFolder.ApplicationData);
            AddOverrideConfig(Environment.SpecialFolder.LocalApplicationData);
        }

        if (environmentVariablePrefix is not null)
        {
            config.AddEnvironmentVariables(environmentVariablePrefix);
        }

        return builder;
    }
}
