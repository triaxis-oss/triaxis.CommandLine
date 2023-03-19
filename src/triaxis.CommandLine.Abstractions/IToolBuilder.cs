namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Hosting;

public interface IToolBuilder : IHostBuilder
{
    string[] Arguments { get; }
    RootCommand RootCommand { get; }
    Command GetCommand(params string[] path);

    new Parser Build();
}
