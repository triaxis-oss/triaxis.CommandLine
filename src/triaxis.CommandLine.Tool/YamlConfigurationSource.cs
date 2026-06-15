namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;

/// <summary>
/// A read-only YAML file configuration source — the YAML counterpart of the built-in
/// <c>JsonConfigurationSource</c>. The YAML engine is YamlDotNet (already pulled in
/// transitively by <c>triaxis.CommandLine.ObjectOutput</c>); no extra dependency is
/// needed. For a writable layer use <see cref="YamlPersistentConfigurationSource"/>.
/// </summary>
public sealed class YamlConfigurationSource : FileConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new YamlConfigurationProvider(this);
    }
}

/// <summary>
/// Reads a YAML file into the flat configuration dictionary. Left unsealed so
/// <see cref="YamlPersistentConfigurationProvider"/> can reuse the parsing and add the
/// writable contract on top.
/// </summary>
internal class YamlConfigurationProvider(FileConfigurationSource source)
    : FileConfigurationProvider(source)
{
    public override void Load(Stream stream)
        => Data = YamlConfigurationParser.Parse(stream);
}

/// <summary>
/// A read-only YAML <see cref="Stream"/> configuration source — the YAML counterpart
/// of the built-in <c>JsonStreamConfigurationSource</c>.
/// </summary>
public sealed class YamlStreamConfigurationSource : StreamConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
        => new YamlStreamConfigurationProvider(this);
}

internal sealed class YamlStreamConfigurationProvider(YamlStreamConfigurationSource source)
    : StreamConfigurationProvider(source)
{
    public override void Load(Stream stream)
        => Data = YamlConfigurationParser.Parse(stream);
}

/// <summary>
/// Turns a YAML document into the flat <c>"A:B:C" → value</c> dictionary, matching the
/// <c>Microsoft.Extensions.Configuration</c> conventions (mappings nest with <c>:</c>,
/// sequences index by position). Shared by every YAML provider in the package.
/// </summary>
internal static class YamlConfigurationParser
{
    public static IDictionary<string, string?> Parse(Stream stream)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var yaml = new YamlStream();
        // The configuration provider owns the stream and disposes it; wrapping it in a
        // `using` StreamReader would close it early (there is no leave-open ctor on
        // netstandard2.0). The undisposed reader only drops its buffer to GC.
        var reader = new StreamReader(stream);
        yaml.Load(reader);

        if (yaml.Documents.Count > 0)
        {
            Flatten(data, yaml.Documents[0].RootNode, "");
        }

        return data;
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
}
