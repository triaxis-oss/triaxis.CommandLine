namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;

public static partial class ToolBuilderExtensions
{
    /// <summary>
    /// Registers <paramref name="relativePath"/> in the per-machine and per-user
    /// configuration folders, calling <paramref name="addFile"/> once per folder with the
    /// matching <see cref="ConfigurationScope"/>:
    /// <see cref="Environment.SpecialFolder.CommonApplicationData"/> →
    /// <see cref="ConfigurationScope.Machine"/>; both
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> and
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> →
    /// <see cref="ConfigurationScope.User"/>. The delegate receives the folder root and
    /// the relative path separately so it can root a watching file provider at the
    /// (existing) folder rather than the file's — possibly absent — parent.
    /// </summary>
    /// <remarks>
    /// The file is registered <em>unconditionally</em>, whether or not it exists yet:
    /// <paramref name="addFile"/> must add it as <c>optional</c> with reload-on-change so
    /// that a file written later — common for user/machine overrides in a long-running
    /// process — is picked up live rather than missed because no watcher was attached.
    /// Format-neutral: the delegate chooses the provider (JSON, YAML, …), keeping
    /// format-specific dependencies out of this package.
    /// </remarks>
    /// <returns>The same builder, for fluent chaining.</returns>
    public static ScopedConfigurationBuilder AddOverrides(
        this ScopedConfigurationBuilder scoped,
        string relativePath,
        Action<IConfigurationBuilder, string, string> addFile)
        => scoped.AddOverrides(relativePath, addFile, Environment.GetFolderPath);

    internal static ScopedConfigurationBuilder AddOverrides(
        this ScopedConfigurationBuilder scoped,
        string relativePath,
        Action<IConfigurationBuilder, string, string> addFile,
        Func<Environment.SpecialFolder, string> resolveFolder)
    {
        void Probe(ConfigurationScope scope, Environment.SpecialFolder folder)
        {
            scoped.Add(scope, cfg => addFile(cfg, resolveFolder(folder), relativePath));
        }

        Probe(ConfigurationScope.Machine, Environment.SpecialFolder.CommonApplicationData);
        Probe(ConfigurationScope.User, Environment.SpecialFolder.ApplicationData);
        Probe(ConfigurationScope.User, Environment.SpecialFolder.LocalApplicationData);
        return scoped;
    }
}
