namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;

/// <summary>
/// A YAML file configuration source whose provider is an
/// <see cref="IPersistentConfigurationProvider"/>, so a scope-targeted
/// <see cref="PersistentConfigurationExtensions.Update">Update</see> can write the
/// layer back to disk. The YAML engine is YamlDotNet (already pulled in by
/// <c>triaxis.CommandLine.ObjectOutput</c>); no extra dependency is needed.
/// </summary>
public sealed class YamlPersistentConfigurationSource : FileConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new YamlPersistentConfigurationProvider(this);
    }
}

internal sealed class YamlPersistentConfigurationProvider(YamlPersistentConfigurationSource source)
    : FileConfigurationProvider(source), IPersistentConfigurationProvider
{
    private readonly Dictionary<string, string?> _dirty = new(StringComparer.OrdinalIgnoreCase);

    public override void Set(string key, string? value)
    {
        base.Set(key, value);
        _dirty[key] = value;
    }

    public override void Load(Stream stream)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var yaml = new YamlStream();
        // FileConfigurationProvider owns the stream and disposes it; wrapping it in a
        // `using` StreamReader would close it early (there is no leave-open ctor on
        // netstandard2.0). The undisposed reader only drops its buffer to GC.
        var reader = new StreamReader(stream);
        yaml.Load(reader);

        if (yaml.Documents.Count > 0)
        {
            Flatten(data, yaml.Documents[0].RootNode, "");
        }

        Data = data;
    }

    private static void Flatten(IDictionary<string, string?> data, YamlNode node, string prefix)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var entry in map.Children)
                {
                    Flatten(data, entry.Value,
                        PersistentConfigurationFile.Join(prefix, ((YamlScalarNode)entry.Key).Value!));
                }
                break;

            case YamlSequenceNode sequence:
                int index = 0;
                foreach (var item in sequence.Children)
                {
                    Flatten(data, item, PersistentConfigurationFile.Join(prefix, index++.ToString()));
                }
                break;

            case YamlScalarNode scalar:
                PersistentConfigurationFile.AddLeaf(data, prefix, scalar.Value);
                break;
        }
    }

    public void Save()
    {
        PersistentConfigurationFile.Rewrite(Source, original =>
            YamlConfigurationEditor.Apply(original, _dirty));
        _dirty.Clear();
        OnReload();
    }
}
