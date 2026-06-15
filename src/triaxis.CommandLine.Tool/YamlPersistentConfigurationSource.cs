namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;

/// <summary>
/// A YAML file configuration source whose provider is an
/// <see cref="IPersistentConfigurationProvider"/>, so a scope-targeted
/// <see cref="PersistentConfigurationExtensions.Update">Update</see> can write the
/// layer back to disk. Reading is the plain <see cref="YamlConfigurationSource"/>
/// behaviour; only <c>Set</c>/<c>Save</c> are added on top.
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
    : YamlConfigurationProvider(source), IPersistentConfigurationProvider
{
    private readonly Dictionary<string, string?> _dirty = new(StringComparer.OrdinalIgnoreCase);

    public override void Set(string key, string? value)
    {
        base.Set(key, value);
        _dirty[key] = value;
    }

    public void Save()
    {
        PersistentConfigurationFile.Rewrite(Source, original =>
            YamlConfigurationEditor.Apply(original, _dirty));
        _dirty.Clear();
        OnReload();
    }
}
