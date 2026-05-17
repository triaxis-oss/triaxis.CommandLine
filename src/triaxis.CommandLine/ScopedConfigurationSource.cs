namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

/// <summary>
/// A single <see cref="IConfigurationSource"/> that exposes the scope-layered,
/// optionally subtree-remapped configuration assembled by
/// <see cref="ScopedConfigurationBuilder"/>. Being one source keeps its internal
/// precedence deterministic regardless of where it sits in an outer builder and
/// lets it replay through <c>ApplyTo</c> as a unit. Each scope's underlying
/// configuration stays live: a reload in any of them is folded back in and
/// propagated to consumers.
/// </summary>
internal sealed class ScopedConfigurationSource(
    IReadOnlyDictionary<ConfigurationScope, IReadOnlyList<Action<IConfigurationBuilder>>> scopes,
    IReadOnlyList<(string From, string? To)> remaps) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new ScopedConfigurationProvider(scopes, remaps);

    private sealed class ScopedConfigurationProvider(
        IReadOnlyDictionary<ConfigurationScope, IReadOnlyList<Action<IConfigurationBuilder>>> scopes,
        IReadOnlyList<(string From, string? To)> remaps) : ConfigurationProvider
    {
        // Built once and kept alive so each scope's providers (and their file
        // watchers) stay active; a reload in any of them re-runs the fold below.
        private List<IConfigurationRoot>? _roots;

        public override void Load()
        {
            if (_roots is null)
            {
                _roots = [];
                // Enum order is ascending specificity (see ConfigurationScope).
                foreach (ConfigurationScope scope in (ConfigurationScope[])Enum.GetValues(typeof(ConfigurationScope)))
                {
                    if (!scopes.TryGetValue(scope, out var actions) || actions.Count == 0)
                    {
                        continue;
                    }

                    var cb = new ConfigurationBuilder();
                    foreach (var action in actions)
                    {
                        action(cb);
                    }

                    IConfigurationRoot root = cb.Build();
                    _roots.Add(root);
                    ChangeToken.OnChange(root.GetReloadToken, Reload);
                }
            }

            // One flat dictionary written in (scope, then within-scope: primary then
            // subtree) order. Last write wins, which reproduces exactly the required
            // precedence: a less specific scope's subtree is overwritten by any more
            // specific scope's primary, while within a scope the subtree overlays it.
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (IConfigurationRoot root in _roots)
            {
                foreach (var kv in root.AsEnumerable())
                {
                    if (kv.Value is not null)
                    {
                        data[kv.Key] = kv.Value;
                    }
                }

                foreach (var (from, to) in remaps)
                {
                    foreach (var kv in root.GetSection(from).AsEnumerable(makePathsRelative: true))
                    {
                        if (kv.Value is null)
                        {
                            continue;
                        }

                        string target = kv.Key.Length == 0
                            ? to ?? string.Empty
                            : string.IsNullOrEmpty(to) ? kv.Key : $"{to}:{kv.Key}";

                        if (target.Length == 0)
                        {
                            // A bare value at the section node remapped onto the root
                            // has nowhere to live — skip it.
                            continue;
                        }

                        data[target] = kv.Value;
                    }
                }
            }

            Data = data;
        }

        private void Reload()
        {
            Load();
            OnReload();
        }
    }
}
