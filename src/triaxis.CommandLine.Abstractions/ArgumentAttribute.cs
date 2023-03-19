namespace triaxis.CommandLine;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ArgumentAttribute : Attribute
{
    public ArgumentAttribute()
    {
    }

    public ArgumentAttribute(string? name = null, string? description = null)
    {
        Name = name;
        Description = description;
    }

    public string? Name { get; set; }
    public string? Description { get; set; }
    public int Order { get; set; } = 0;

    private bool? _required;
    public bool Required
    {
        get => _required ?? false;
        set => _required = value;
    }

    public bool RequiredIsSet => _required.HasValue;
}
