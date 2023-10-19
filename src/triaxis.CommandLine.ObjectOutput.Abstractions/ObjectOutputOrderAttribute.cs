namespace triaxis.CommandLine.ObjectOutput;

[AttributeUsage(AttributeTargets.Property)]
public class ObjectOutputOrderAttribute : Attribute
{
    public ObjectOutputOrderAttribute(double order)
    {
        Order = order;
    }
    public double Order { get; }
}
