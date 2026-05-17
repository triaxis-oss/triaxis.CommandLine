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
        builder.UseDefaultLogging();
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
    /// <param name="configure">
    /// Optional hook to add further scoped sources (e.g. an <see cref="ConfigurationScope.Override"/>
    /// file) or subtree <see cref="ScopedConfigurationBuilder.Remap">remaps</see>.
    /// </param>
    /// <remarks>
    /// Sources are tagged by <see cref="ConfigurationScope"/> and assembled into a single
    /// scope-layered source: <c>appsettings.json</c> is <see cref="ConfigurationScope.Builtin"/>,
    /// the override file probed under the all-users data folder is
    /// <see cref="ConfigurationScope.Machine"/>, the per-user data folders are
    /// <see cref="ConfigurationScope.User"/>, and environment variables are
    /// <see cref="ConfigurationScope.EnvironmentVariables"/>. The effective precedence is
    /// unchanged from a flat source list; the machine probe is purely additive.
    /// Targets <see cref="IHostBuilder"/> so the same configuration bootstrap can be reused
    /// by alternate hosts (e.g. <c>WebApplication.CreateBuilder(args).Host.UseDefaultConfiguration(...)</c>).
    /// For <see cref="IToolBuilder"/> an overload of the same name preserves the fluent CLI chain.
    /// </remarks>
    public static IHostBuilder UseDefaultConfiguration(this IHostBuilder builder,
        string? configOverridePath = null,
        string? environmentVariablePrefix = null,
        Action<ScopedConfigurationBuilder>? configure = null)
    {
        builder.UseScopedConfiguration(scoped =>
        {
            scoped.Add(ConfigurationScope.Builtin, config =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: true);
            });

            if (configOverridePath is not null)
            {
                void Probe(ConfigurationScope scope, Environment.SpecialFolder folder)
                {
                    scoped.Add(scope, config =>
                    {
                        var path = Path.Combine(Environment.GetFolderPath(folder), configOverridePath);
                        if (File.Exists(path))
                        {
                            config.AddJsonFile(new PhysicalFileProvider(Path.GetDirectoryName(path)!), Path.GetFileName(path), optional: false, reloadOnChange: false);
                        }
                    });
                }

                Probe(ConfigurationScope.Machine, Environment.SpecialFolder.CommonApplicationData);
                Probe(ConfigurationScope.User, Environment.SpecialFolder.ApplicationData);
                Probe(ConfigurationScope.User, Environment.SpecialFolder.LocalApplicationData);
            }

            if (environmentVariablePrefix is not null)
            {
                scoped.Add(ConfigurationScope.EnvironmentVariables,
                    config => config.AddEnvironmentVariables(environmentVariablePrefix));
            }

            configure?.Invoke(scoped);
        });

        return builder;
    }

    public static IToolBuilder UseDefaultConfiguration(this IToolBuilder builder,
        string? configOverridePath = null,
        string? environmentVariablePrefix = null,
        Action<ScopedConfigurationBuilder>? configure = null)
    {
        ((IHostBuilder)builder).UseDefaultConfiguration(configOverridePath, environmentVariablePrefix, configure);
        return builder;
    }

    /// <summary>
    /// Adds <paramref name="fileName"/> from <see cref="AppContext.BaseDirectory"/> to the
    /// <see cref="ConfigurationScope.Builtin"/> scope as an optional JSON file — the
    /// application defaults shipped alongside the executable.
    /// </summary>
    /// <returns>The same builder, for fluent chaining.</returns>
    public static ScopedConfigurationBuilder AddBuiltinConfiguration(
        this ScopedConfigurationBuilder scoped,
        string fileName = "appsettings.json",
        bool reloadOnChange = true)
        => scoped.Add(ConfigurationScope.Builtin, cfg =>
        {
            cfg.SetBasePath(AppContext.BaseDirectory);
            cfg.AddJsonFile(fileName, optional: true, reloadOnChange: reloadOnChange);
        });

    /// <summary>
    /// Registers the JSON override file <paramref name="relativePath"/> (extension
    /// included, so callers pick <c>.json</c> freely) in the per-machine and per-user
    /// folders → the <see cref="ConfigurationScope.Machine"/> /
    /// <see cref="ConfigurationScope.User"/> scopes. The JSON flavour of
    /// <see cref="AddOverrides(ScopedConfigurationBuilder, string, Action{IConfigurationBuilder, string, string})"/>:
    /// the file is added as optional and watched, so one written after start-up is
    /// picked up live.
    /// </summary>
    /// <returns>The same builder, for fluent chaining.</returns>
    public static ScopedConfigurationBuilder AddJsonOverrides(
        this ScopedConfigurationBuilder scoped,
        string relativePath,
        bool reloadOnChange = true)
        => scoped.AddOverrides(relativePath, (cfg, directory, fileName) =>
        {
            // PhysicalFileProvider throws if its root is missing; the watchable case is
            // an absent *file* in an existing config folder, so only that is supported.
            if (Directory.Exists(directory))
            {
                cfg.AddJsonFile(
                    new PhysicalFileProvider(directory),
                    fileName,
                    optional: true,
                    reloadOnChange: reloadOnChange);
            }
        });

    /// <summary>
    /// Adds environment variables (filtered by <paramref name="prefix"/>) to the
    /// <see cref="ConfigurationScope.EnvironmentVariables"/> scope.
    /// </summary>
    /// <returns>The same builder, for fluent chaining.</returns>
    public static ScopedConfigurationBuilder AddEnvironmentOverrides(
        this ScopedConfigurationBuilder scoped,
        string prefix)
        => scoped.Add(ConfigurationScope.EnvironmentVariables,
            cfg => cfg.AddEnvironmentVariables(prefix));
}
