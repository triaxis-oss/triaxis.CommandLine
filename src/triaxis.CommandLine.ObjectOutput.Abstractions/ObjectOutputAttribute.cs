namespace triaxis.CommandLine.ObjectOutput;

[AttributeUsage(AttributeTargets.Property)]
public class ObjectOutputAttribute : Attribute
{
    public ObjectOutputAttribute(double? order = null, ObjectFieldVisibility? visibility = null)
    {
        Order = order;
        Visibility = visibility;
    }

    public double? Order { get; set; }
    public ObjectFieldVisibility? Visibility { get; set; }
}
