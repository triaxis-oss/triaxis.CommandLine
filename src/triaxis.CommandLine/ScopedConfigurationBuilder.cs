namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Collects configuration sources grouped by <see cref="ConfigurationScope"/> plus
/// optional subtree remap rules, and assembles them into a single layered
/// configuration source whose precedence honours the scope ordering.
/// </summary>
/// <remarks>
/// The merged result is built so that, for every key, a value defined in a more
/// specific scope wins over one from a less specific scope. A remapped subtree
/// overlays its <em>own</em> scope's primary tree, but never overrides a primary
/// value from a more specific scope — so a default environment-section overlay
/// cannot clobber an explicit user/override setting.
/// </remarks>
public sealed class ScopedConfigurationBuilder
{
    private readonly Dictionary<ConfigurationScope, List<Action<IConfigurationBuilder>>> _scopes = [];
    private readonly List<(string From, string? To)> _remaps = [];

    /// <summary>
    /// Adds configuration sources to <paramref name="scope"/>. The delegate runs against
    /// a dedicated builder for that scope at load time, so file base paths resolve the
    /// same way they would on a standalone <see cref="ConfigurationBuilder"/>. May be
    /// called repeatedly; sources accumulate in call order within the scope.
    /// </summary>
    /// <returns>The same builder, for fluent chaining.</returns>
    public ScopedConfigurationBuilder Add(ConfigurationScope scope, Action<IConfigurationBuilder> configureSources)
    {
        if (!_scopes.TryGetValue(scope, out var list))
        {
            _scopes[scope] = list = [];
        }
        list.Add(configureSources);
        return this;
    }

    /// <summary>
    /// Declares that the subtree at <paramref name="fromPath"/> is overlaid onto
    /// <paramref name="toPath"/> (the root tree when <paramref name="toPath"/> is
    /// <see langword="null"/> or empty). The remap is applied independently per scope:
    /// each scope overlays its own subtree onto its own primary tree, and the usual
    /// scope precedence then decides the final value — so a less specific scope's
    /// subtree can never override a more specific scope's primary value.
    /// </summary>
    /// <returns>The same builder, for fluent chaining.</returns>
    public ScopedConfigurationBuilder Remap(string fromPath, string? toPath = null)
    {
        _remaps.Add((fromPath, toPath));
        return this;
    }

    internal ScopedConfigurationSource BuildSource()
    {
        var snapshot = new Dictionary<ConfigurationScope, IReadOnlyList<Action<IConfigurationBuilder>>>();
        foreach (var entry in _scopes)
        {
            snapshot[entry.Key] = entry.Value.ToArray();
        }
        return new ScopedConfigurationSource(snapshot, _remaps.ToArray());
    }
}
