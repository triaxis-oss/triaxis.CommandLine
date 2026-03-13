// Use source-generated command registration (no runtime assembly scanning)
Tool.CreateBuilder(args)
    .UseDefaults(commandRegistration: b => b.AddGeneratedCommands())
    .Run();
