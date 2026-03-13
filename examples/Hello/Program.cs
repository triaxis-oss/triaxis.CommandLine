using triaxis.CommandLine.Generated;

// Use source-generated command tree instead of reflection-based UseDefaults
Tool.CreateBuilder(args)
    .AddGeneratedCommands()
    .UseVerbosityOptions()
    .Run();
