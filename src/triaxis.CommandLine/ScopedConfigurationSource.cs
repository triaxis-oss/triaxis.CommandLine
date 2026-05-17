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
        IReadOnlyList<(string From, string? To)> remaps) : ConfigurationProvider, IScopedPersistentLookup
    {
        // Built once and kept alive so each scope's providers (and their file
        // watchers) stay active; a reload in any of them re-runs the fold below.
        // The scope tag is retained so a scope-targeted write can find its
        // writable provider — the flat fold below otherwise erases scope identity.
        private List<(ConfigurationScope Scope, IConfigurationRoot Root)>? _roots;

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
                    _roots.Add((scope, root));
                    ChangeToken.OnChange(root.GetReloadToken, Reload);
                }
            }

            // One flat dictionary written in (scope, then within-scope: primary then
            // subtree) order. Last write wins, which reproduces exactly the required
            // precedence: a less specific scope's subtree is overwritten by any more
            // specific scope's primary, while within a scope the subtree overlays it.
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (_, root) in _roots)
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

        public IPersistentConfigurationProvider GetPersistentProvider(ConfigurationScope scope)
        {
            if (_roots is null)
            {
                Load();
            }

            IConfigurationRoot? root = null;
            foreach (var (s, r) in _roots!)
            {
                if (s == scope)
                {
                    root = r;
                    break;
                }
            }

            if (root is null)
            {
                throw new InvalidOperationException(
                    $"No configuration sources are registered for scope '{scope}'.");
            }

            // Last writable provider wins, matching within-scope precedence (a later
            // source in a scope overrides an earlier one).
            IPersistentConfigurationProvider? persistent = null;
            foreach (var provider in root.Providers)
            {
                if (provider is IPersistentConfigurationProvider p)
                {
                    persistent = p;
                }
            }

            return persistent ?? throw new InvalidOperationException(
                $"Scope '{scope}' has no writable configuration source. Register an " +
                $"{nameof(IPersistentConfigurationProvider)} for that scope through UseScopedConfiguration.");
        }
    }
}

/// <summary>
/// Surfaced by the scope-layered configuration provider so a scope-targeted write
/// can locate the writable provider for a given <see cref="ConfigurationScope"/> —
/// the flat fold that merges scopes otherwise erases scope identity.
/// </summary>
internal interface IScopedPersistentLookup
{
    /// <exception cref="InvalidOperationException">
    /// The scope has no registered sources, or none of them is writable.
    /// </exception>
    IPersistentConfigurationProvider GetPersistentProvider(ConfigurationScope scope);
}
