namespace triaxis.CommandLine;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class OptionAttribute : CommandlineAttribute
{
    public OptionAttribute()
    {
    }

    public OptionAttribute(string? name = null, params string[] aliases)
    {
        Name = name;
        Aliases = aliases;
    }

    public string[]? Aliases { get; set; }
    public bool Required { get; set; }
}
