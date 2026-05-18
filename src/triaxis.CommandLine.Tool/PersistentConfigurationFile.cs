namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Shared helpers for the writable JSON/YAML file providers: resolving the physical
/// path to persist to, and turning the flat <see cref="ConfigurationProvider.Data"/>
/// dictionary back into the nested shape a settings file expects.
/// </summary>
internal static class PersistentConfigurationFile
{
    /// <summary>
    /// Reads the source's current on-disk text (empty when the file does not exist
    /// yet), lets <paramref name="edit"/> produce the new content, and writes it back.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The provider is not backed by a physical file.
    /// </exception>
    public static void Rewrite(FileConfigurationSource source, Func<string, string> edit)
    {
        string path = ResolvePhysicalPath(source);
        string original = File.Exists(path) ? File.ReadAllText(path) : "";
        string updated = edit(original);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated);
    }

    /// <exception cref="InvalidOperationException">
    /// The provider is not backed by a physical file (e.g. an in-memory file provider),
    /// so there is nowhere to persist to.
    /// </exception>
    private static string ResolvePhysicalPath(FileConfigurationSource source)
    {
        if (source.FileProvider is { } provider && source.Path is { } path
            && provider.GetFileInfo(path).PhysicalPath is { } physical)
        {
            return physical;
        }

        throw new InvalidOperationException(
            $"Cannot persist '{source.Path}': the configured file provider does " +
            "not expose a physical path.");
    }

    /// <summary>
    /// Rebuilds the nested object graph (string leaves, nested
    /// <see cref="SortedDictionary{TKey, TValue}"/> branches) from the flat
    /// <c>"A:B:C" → value</c> configuration keys. Keys are emitted sorted so the
    /// written file stays diff-stable. A <see langword="null"/> value means the key
    /// was cleared, so it is dropped rather than written.
    /// </summary>
    public static SortedDictionary<string, object?> BuildTree(
        IEnumerable<KeyValuePair<string, string?>> data)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in data)
        {
            if (kv.Value is null)
            {
                continue;
            }

            string[] segments = kv.Key.Split(':');
            var node = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (node.TryGetValue(segments[i], out var child)
                    && child is SortedDictionary<string, object?> branch)
                {
                    node = branch;
                }
                else
                {
                    branch = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    node[segments[i]] = branch;
                    node = branch;
                }
            }

            node[segments[segments.Length - 1]] = kv.Value;
        }

        return root;
    }

    /// <summary>
    /// Flattens a parsed document node back to configuration keys, matching the
    /// <c>Microsoft.Extensions.Configuration</c> conventions (objects nest with
    /// <c>:</c>, arrays index by position). Used by both file readers.
    /// </summary>
    public static void AddLeaf(IDictionary<string, string?> data, string path, string? value)
    {
        // A bare scalar at the document root has no key to live under — skip it.
        if (path.Length > 0)
        {
            data[path] = value;
        }
    }

    public static string Join(string prefix, string key)
        => prefix.Length == 0 ? key : $"{prefix}:{key}";
}
