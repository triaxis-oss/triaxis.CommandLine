namespace triaxis.CommandLine.ObjectOutput;

[AttributeUsage(AttributeTargets.Property)]
public class ObjectOutputAttribute : Attribute
{
    public ObjectOutputAttribute()
    {
    }

    public ObjectOutputAttribute(double order)
    {
        Order = order;
    }

    public ObjectOutputAttribute(double order, ObjectFieldVisibility visibility)
    {
        Order = order;
        Visibility = visibility;
    }

    public double? Order { get; set; }
    public ObjectFieldVisibility? Visibility { get; set; }
}
