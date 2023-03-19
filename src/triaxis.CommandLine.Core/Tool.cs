using System.Reflection;

namespace triaxis.CommandLine;

public static class Tool
{
    public static IToolBuilder CreateBuilder(string[] args)
    {
        return new ToolBuilder(args);
    }
}
