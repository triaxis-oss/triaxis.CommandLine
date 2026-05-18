namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

/// <summary>
/// Registers writable JSON/YAML configuration files — the read side behaves exactly
/// like <c>AddJsonFile</c>, but the provider also implements
/// <see cref="IPersistentConfigurationProvider"/> so a scope-targeted
/// <see cref="PersistentConfigurationExtensions.Update">Update</see> can write it back.
/// </summary>
/// <remarks>
/// <c>Save</c> applies the <em>minimal</em> change: only the keys mutated since the
/// last save are patched into the file, so comments, whitespace, key order, and every
/// untouched value survive byte-for-byte. Missing keys are inserted (creating any
/// missing parent objects/mappings), and a JSON array that has to gain a
/// non-positional key is rewritten in place to an object. Written values are
/// string-typed (the configuration model has no other type). When there is no file
/// yet — or the document uses a shape the editor will not touch (flow-style YAML,
/// anchors/aliases, a non-object root) — a fresh canonical document is written
/// instead.
/// </remarks>
public static class PersistentConfigurationBuilderExtensions
{
    /// <param name="fileProvider">
    /// Root the watcher at the (existing) folder rather than a possibly-absent file's
    /// parent — same rationale as <c>AddOverrides</c>. <see langword="null"/> falls back
    /// to the builder's default file provider.
    /// </param>
    public static IConfigurationBuilder AddPersistentJsonFile(
        this IConfigurationBuilder builder,
        IFileProvider? fileProvider,
        string path,
        bool optional,
        bool reloadOnChange)
        => builder.Add<JsonPersistentConfigurationSource>(s =>
        {
            s.FileProvider = fileProvider;
            s.Path = path;
            s.Optional = optional;
            s.ReloadOnChange = reloadOnChange;
        });

    public static IConfigurationBuilder AddPersistentJsonFile(
        this IConfigurationBuilder builder,
        string path,
        bool optional = false,
        bool reloadOnChange = false)
        => builder.AddPersistentJsonFile(null, path, optional, reloadOnChange);

    /// <inheritdoc cref="AddPersistentJsonFile(IConfigurationBuilder, IFileProvider, string, bool, bool)"/>
    public static IConfigurationBuilder AddPersistentYamlFile(
        this IConfigurationBuilder builder,
        IFileProvider? fileProvider,
        string path,
        bool optional,
        bool reloadOnChange)
        => builder.Add<YamlPersistentConfigurationSource>(s =>
        {
            s.FileProvider = fileProvider;
            s.Path = path;
            s.Optional = optional;
            s.ReloadOnChange = reloadOnChange;
        });

    public static IConfigurationBuilder AddPersistentYamlFile(
        this IConfigurationBuilder builder,
        string path,
        bool optional = false,
        bool reloadOnChange = false)
        => builder.AddPersistentYamlFile(null, path, optional, reloadOnChange);
}
