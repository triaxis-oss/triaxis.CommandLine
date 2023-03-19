namespace triaxis.CommandLine;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class OptionAttribute : Attribute
{
    public OptionAttribute()
    {
    }

    public OptionAttribute(string? name = null, params string[] aliases)
    {
        Name = name;
        Aliases = aliases;
    }

    public string? Name { get; set; }
    public string[]? Aliases { get; set; }
    public string? Description { get; set; }
    public bool Required { get; set; }
    public double Order { get; set; } = 0;
}
