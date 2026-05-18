namespace triaxis.CommandLine;

using System.Text.Json;
using Microsoft.Extensions.Configuration;

/// <summary>
/// A JSON file configuration source whose provider is an
/// <see cref="IPersistentConfigurationProvider"/>, so a scope-targeted
/// <see cref="PersistentConfigurationExtensions.Update">Update</see> can write the
/// layer back to disk. Reading (including optional/reload-on-change) is the standard
/// <see cref="FileConfigurationSource"/> behaviour.
/// </summary>
public sealed class JsonPersistentConfigurationSource : FileConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new JsonPersistentConfigurationProvider(this);
    }
}

internal sealed class JsonPersistentConfigurationProvider(JsonPersistentConfigurationSource source)
    : FileConfigurationProvider(source), IPersistentConfigurationProvider
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Keys mutated since the last Save — only these are patched into the file, so
    // everything the caller did not touch (including comments) is left verbatim.
    private readonly Dictionary<string, string?> _dirty = new(StringComparer.OrdinalIgnoreCase);

    public override void Set(string key, string? value)
    {
        base.Set(key, value);
        _dirty[key] = value;
    }

    public override void Load(Stream stream)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // An existing-but-empty file is valid (the override hasn't been written yet);
        // JsonDocument.Parse would throw on it.
        if (!(stream.CanSeek && stream.Length == 0))
        {
            using var doc = JsonDocument.Parse(stream, ReadOptions);
            Flatten(data, doc.RootElement, "");
        }

        Data = data;
    }

    private static void Flatten(IDictionary<string, string?> data, JsonElement element, string prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(data, property.Value, PersistentConfigurationFile.Join(prefix, property.Name));
                }
                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(data, item, PersistentConfigurationFile.Join(prefix, index++.ToString()));
                }
                break;

            case JsonValueKind.Null:
                PersistentConfigurationFile.AddLeaf(data, prefix, null);
                break;

            case JsonValueKind.String:
                PersistentConfigurationFile.AddLeaf(data, prefix, element.GetString());
                break;

            default:
                PersistentConfigurationFile.AddLeaf(data, prefix, element.GetRawText());
                break;
        }
    }

    public void Save()
    {
        PersistentConfigurationFile.Rewrite(Source, original =>
            JsonConfigurationEditor.Apply(original, _dirty));
        _dirty.Clear();
        OnReload();
    }
}
