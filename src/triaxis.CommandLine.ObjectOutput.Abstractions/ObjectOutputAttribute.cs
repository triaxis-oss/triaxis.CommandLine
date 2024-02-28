namespace triaxis.CommandLine.ObjectOutput;

[AttributeUsage(AttributeTargets.Property)]
public class ObjectOutputAttribute : Attribute
{
    public ObjectOutputAttribute()
    {
    }

    public ObjectOutputAttribute(ObjectFieldVisibility visibility)
    {
        Visibility = visibility;
    }

    public string? Before { get; set; }
    public string? After { get; set; }
    public ObjectFieldVisibility? Visibility { get; set; }
}
