namespace triaxis.CommandLine;

public class CommandlineAttribute : Attribute
{
    internal CommandlineAttribute()
    {
    }

    public string? Name { get; set; }
    public string? Description { get; set; }
    public double Order { get; set; } = 0;
}
