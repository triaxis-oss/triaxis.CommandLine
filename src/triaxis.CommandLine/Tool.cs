namespace triaxis.CommandLine;

public static class Tool
{
    public static IToolBuilder CreateBuilder(IEnumerable<string> args)
    {
        return new ToolBuilder(args);
    }
}
