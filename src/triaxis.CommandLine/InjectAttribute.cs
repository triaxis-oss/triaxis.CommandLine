namespace triaxis.CommandLine;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class InjectAttribute : Attribute
{
    public InjectAttribute()
    {
    }

    public InjectAttribute(Type type)
    {
        Type = type;
    }

    public Type? Type { get; set; }
}
