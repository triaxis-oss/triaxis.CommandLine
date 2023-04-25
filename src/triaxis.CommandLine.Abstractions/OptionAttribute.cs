namespace triaxis.CommandLine;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class OptionAttribute : CommandlineAttribute
{
    public OptionAttribute()
    {
    }

    public OptionAttribute(string? name)
    {
        Name = name;
    }

    public OptionAttribute(string? name, params string[] aliases)
    {
        Name = name;
        Aliases = aliases;
    }

    public string[]? Aliases { get; set; }
    public bool Required { get; set; }
}
