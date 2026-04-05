#!/usr/bin/env dotnet
#:project ../src/triaxis.CommandLine.Tool/triaxis.CommandLine.Tool.csproj

using Microsoft.Extensions.Logging;
using triaxis.CommandLine;

[Command("greet", Description = "Greets someone")]
public class GreetCommand : LoggingCommand
{
    [Option("--name", "-n", Description = "Name of the person to greet")]
    private readonly string _name = "World";

    public void Execute()
    {
        Logger.LogDebug("Greeting {Name}...", _name);
        Console.WriteLine($"Hello {_name}!");
    }
}
