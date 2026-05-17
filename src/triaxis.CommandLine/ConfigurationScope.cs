namespace triaxis.CommandLine;

/// <summary>
/// Precedence layers a scoped configuration is built from, ordered from least to
/// most specific. A value defined in a more specific scope always wins over one
/// from a less specific scope — including over a remapped subtree value from any
/// less specific scope (see <see cref="ScopedConfigurationBuilder.Remap"/>).
/// </summary>
public enum ConfigurationScope
{
    /// <summary>Defaults shipped with the application (e.g. <c>appsettings.json</c>).</summary>
    Builtin,

    /// <summary>Machine-wide configuration shared by all users.</summary>
    Machine,

    /// <summary>Per-user configuration.</summary>
    User,

    /// <summary>Environment variables.</summary>
    EnvironmentVariables,

    /// <summary>An explicit override (e.g. a <c>--config</c> file). The most specific scope.</summary>
    Override,
}
