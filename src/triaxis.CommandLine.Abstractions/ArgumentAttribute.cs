namespace triaxis.CommandLine;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ArgumentAttribute : CommandlineAttribute
{
    public ArgumentAttribute()
    {
    }

    public ArgumentAttribute(string? name = null, string? description = null)
    {
        Name = name;
        Description = description;
    }

    private bool? _required;
    public bool Required
    {
        get => _required ?? false;
        set => _required = value;
    }

    public bool RequiredIsSet => _required.HasValue;
}
