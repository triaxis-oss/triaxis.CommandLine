namespace triaxis.CommandLine;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

public static class ToolBuilderExtensions
{
    /// <summary>
    /// Opinionated one-liner for hand-written entry points. Wires up Serilog, verbosity
    /// options, object output, command discovery, and the default configuration sources.
    /// </summary>
    /// <remarks>
    /// The source-generated entry point does <b>not</b> call this method; it chains the
    /// individual helpers directly so projects without output-producing commands can
    /// trim <c>triaxis.CommandLine.ObjectOutput</c> (and YamlDotNet) out of the published
    /// binary. Keep that in mind if you add more work here — it won't be reachable from
    /// the generated <c>Main</c>.
    /// </remarks>
    public static IToolBuilder UseDefaults(this IToolBuilder builder,
        string? configOverridePath = null,
        string? environmentVariablePrefix = null,
        Assembly? commandsAssembly = null)
    {
        // Run command discovery first so the recursive options added by
        // UseVerbosityOptions / UseObjectOutput are appended after every local
        // option in the root command's option list.
        builder.AddCommandsFromAssembly(commandsAssembly ?? Assembly.GetCallingAssembly());
        builder.UseSerilog();
        builder.UseVerbosityOptions();
        builder.UseObjectOutput();
        builder.UseDefaultConfiguration(configOverridePath, environmentVariablePrefix);
        return builder;
    }

    /// <summary>
    /// Wires up the default configuration sources: <c>appsettings.json</c> from the app
    /// base directory, an optional per-user override file, and environment variables with
    /// the supplied prefix.
    /// </summary>
    /// <param name="configOverridePath">
    /// Optional relative path (e.g. <c>"MyTool/appsettings.json"</c>) probed under both
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> and
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
    /// </param>
    /// <param name="environmentVariablePrefix">
    /// Optional prefix for environment-variable configuration (e.g. <c>"MYTOOL_"</c>).
    /// </param>
    /// <remarks>
    /// Targets <see cref="IHostBuilder"/> so the same configuration bootstrap can be reused
    /// by alternate hosts (e.g. <c>WebApplication.CreateBuilder(args).Host.UseDefaultConfiguration(...)</c>).
    /// For <see cref="IToolBuilder"/> an overload of the same name preserves the fluent CLI chain.
    /// </remarks>
    public static IHostBuilder UseDefaultConfiguration(this IHostBuilder builder,
        string? configOverridePath = null,
        string? environmentVariablePrefix = null)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.SetBasePath(AppContext.BaseDirectory);
            config.AddJsonFile("appsettings.json", optional: true);

            if (configOverridePath is not null)
            {
                void AddOverrideConfig(Environment.SpecialFolder folder)
                {
                    var path = Path.Combine(Environment.GetFolderPath(folder), configOverridePath);
                    if (File.Exists(path))
                    {
                        config.AddJsonFile(new PhysicalFileProvider(Path.GetDirectoryName(path)!), Path.GetFileName(path), optional: false, reloadOnChange: false);
                    }
                }

                AddOverrideConfig(Environment.SpecialFolder.ApplicationData);
                AddOverrideConfig(Environment.SpecialFolder.LocalApplicationData);
            }

            if (environmentVariablePrefix is not null)
            {
                config.AddEnvironmentVariables(environmentVariablePrefix);
            }
        });

        return builder;
    }

    public static IToolBuilder UseDefaultConfiguration(this IToolBuilder builder,
        string? configOverridePath = null,
        string? environmentVariablePrefix = null)
    {
        ((IHostBuilder)builder).UseDefaultConfiguration(configOverridePath, environmentVariablePrefix);
        return builder;
    }
}
