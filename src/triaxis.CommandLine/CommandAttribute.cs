namespace triaxis.CommandLine;

[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = true)]
public class CommandAttribute : Attribute
{
    public CommandAttribute(params string[] path)
    {
        Path = path;
    }

    public string[] Path { get; }
    public string[]? Aliases { get; set; }
    public string? Description { get; set; }
}
