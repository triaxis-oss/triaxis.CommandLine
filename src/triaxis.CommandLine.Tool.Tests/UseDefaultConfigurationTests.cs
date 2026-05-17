namespace triaxis.CommandLine.ToolTests;

using System.CommandLine;
using System.CommandLine.Help;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[TestFixture]
public class UseDefaultConfigurationTests
{
    [Test]
    public void UseDefaultConfiguration_WithNoArgs_StillExposesConfiguration()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.UseDefaultConfiguration();
        Assert.That(result, Is.SameAs(builder));
        Assert.That(builder.Configuration, Is.Not.Null);
    }

    [Test]
    public async Task UseDefaultConfiguration_WithEnvironmentVariablePrefix_ReadsFromEnvironment()
    {
        Environment.SetEnvironmentVariable("TXCFG_mykey", "myvalue");
        try
        {
            var builder = Tool.CreateBuilder(["greet"])
                .UseSerilog()
                .UseVerbosityOptions()
                .UseDefaultConfiguration(environmentVariablePrefix: "TXCFG_")
                .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

            await builder.RunAsync();
            Assert.That(builder.Configuration["mykey"], Is.EqualTo("myvalue"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TXCFG_mykey", null);
        }
    }

    [Test]
    public async Task GeneratedMainShape_NoObjectOutput_HasNoOutputOption()
    {
        // This mirrors what the source generator emits when no command produces output:
        // the chain omits .UseObjectOutput(), so --output should not be wired up.
        var builder = Tool.CreateBuilder([])
            .UseSerilog()
            .UseVerbosityOptions()
            .UseDefaultConfiguration()
            .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        Assert.That(names, Does.Not.Contain("--output"));
        Assert.That(names, Does.Contain("--verbosity"));
        await Task.CompletedTask;
    }

    [Test]
    public void GeneratedMainShape_WithObjectOutput_HasOutputOption()
    {
        // And when .UseObjectOutput() is present, --output is wired up alongside the
        // rest of the defaults.
        var builder = Tool.CreateBuilder([])
            .UseSerilog()
            .UseVerbosityOptions()
            .UseObjectOutput()
            .UseDefaultConfiguration()
            .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        Assert.That(names, Does.Contain("--output"));
        Assert.That(names, Does.Contain("--verbosity"));
    }

    [Test]
    public void RootOptions_EndWithHelpAndVersionAfterUserRecursiveOptions()
    {
        // Root Options list should be: [local..., user-recursive..., --help, --version].
        // Subcommands inherit recursive options in this exact order when help renders,
        // so this is the single source of truth for the rendered ordering.
        var builder = Tool.CreateBuilder([])
            .UseSerilog()
            .UseVerbosityOptions()
            .UseObjectOutput()
            .AddCommandsFromAssembly(typeof(RootWithLocalOption).Assembly);

        var options = builder.RootCommand.Options.ToList();
        var helpIndex = options.FindIndex(o => o is HelpOption);
        var versionIndex = options.FindIndex(o => o is VersionOption);
        foreach (var userRecursive in new[] { "--verbosity", "-v", "-q", "--output" })
        {
            var idx = options.FindIndex(o => o.Name == userRecursive);
            Assert.That(idx, Is.LessThan(helpIndex), $"{userRecursive} must appear before --help on root.");
            Assert.That(idx, Is.LessThan(versionIndex), $"{userRecursive} must appear before --version on root.");
        }
    }

    [Test]
    public void AddRecursiveOption_RegistersParentExactlyOnce()
    {
        // ChildSymbolList's Remove+Add and indexer setter each re-run parent
        // registration, which would accumulate duplicate parent references. The
        // AddRecursiveOption helper must land the option at the final position via
        // a single Insert — no move operations.
        var builder = Tool.CreateBuilder([]);
        var opt = new Option<string>("--custom") { Recursive = true };
        builder.AddRecursiveOption(opt);

        Assert.That(opt.Parents.Count(), Is.EqualTo(1),
            "A freshly registered recursive option should have exactly one parent.");
    }

    [Test]
    public void UseDefaultConfiguration_OnPlainHostBuilder_AppliesEnvironmentVariables()
    {
        // The extension should be usable on any IHostBuilder, so an alternate host
        // (e.g. WebApplication.CreateBuilder(args).Host) can reuse the same
        // configuration bootstrap.
        Environment.SetEnvironmentVariable("TXHOSTCFG_plainkey", "plainvalue");
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseDefaultConfiguration(environmentVariablePrefix: "TXHOSTCFG_")
                .Build();

            var configuration = host.Services.GetRequiredService<IConfiguration>();
            Assert.That(configuration["plainkey"], Is.EqualTo("plainvalue"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TXHOSTCFG_plainkey", null);
        }
    }

    [Test]
    public async Task AddEnvironmentOverrides_ReadsPrefixedEnvironment()
    {
        Environment.SetEnvironmentVariable("TXSCOPED_envkey", "envvalue");
        try
        {
            var builder = Tool.CreateBuilder(["greet"])
                .UseSerilog()
                .UseVerbosityOptions()
                .UseScopedConfiguration(s => s.AddEnvironmentOverrides("TXSCOPED_"))
                .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

            await builder.RunAsync();
            Assert.That(builder.Configuration["envkey"], Is.EqualTo("envvalue"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TXSCOPED_envkey", null);
        }
    }

    [Test]
    public async Task AddBuiltinConfiguration_ReadsFileFromBaseDirectory()
    {
        var name = $"appsettings.{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllText(path, """{ "builtinKey": "fromBaseDir" }""");
        try
        {
            var builder = Tool.CreateBuilder(["greet"])
                .UseSerilog()
                .UseVerbosityOptions()
                .UseScopedConfiguration(s => s.AddBuiltinConfiguration(name, reloadOnChange: false))
                .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

            await builder.RunAsync();
            Assert.That(builder.Configuration["builtinKey"], Is.EqualTo("fromBaseDir"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task AddJsonOverrides_UserFileOverridesBuiltin()
    {
        var builtinName = $"appsettings.{Guid.NewGuid():N}.json";
        var builtinPath = Path.Combine(AppContext.BaseDirectory, builtinName);
        var overrideName = $"override.{Guid.NewGuid():N}.json";
        var overridePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), overrideName);
        Directory.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        File.WriteAllText(builtinPath, """{ "shared": "builtin" }""");
        File.WriteAllText(overridePath, """{ "shared": "user" }""");
        try
        {
            var builder = Tool.CreateBuilder(["greet"])
                .UseSerilog()
                .UseVerbosityOptions()
                .UseScopedConfiguration(s => s
                    .AddBuiltinConfiguration(builtinName, reloadOnChange: false)
                    .AddJsonOverrides(overrideName, reloadOnChange: false))
                .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

            await builder.RunAsync();
            Assert.That(builder.Configuration["shared"], Is.EqualTo("user"),
                "the per-user override (User scope) wins over the Builtin appsettings");
        }
        finally
        {
            File.Delete(builtinPath);
            File.Delete(overridePath);
        }
    }

    [Test]
    public async Task AddJsonOverrides_AbsentFileIsRegisteredHarmlessly()
    {
        // The override is registered even when missing (so a watcher exists for a file
        // written later); an absent optional file must not throw or shadow the builtin.
        var builtinName = $"appsettings.{Guid.NewGuid():N}.json";
        var builtinPath = Path.Combine(AppContext.BaseDirectory, builtinName);
        File.WriteAllText(builtinPath, """{ "shared": "builtin" }""");
        try
        {
            var builder = Tool.CreateBuilder(["greet"])
                .UseSerilog()
                .UseVerbosityOptions()
                .UseScopedConfiguration(s => s
                    .AddBuiltinConfiguration(builtinName, reloadOnChange: false)
                    .AddJsonOverrides($"never-written.{Guid.NewGuid():N}.json"))
                .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

            await builder.RunAsync();
            Assert.That(builder.Configuration["shared"], Is.EqualTo("builtin"),
                "an absent optional override neither throws nor hides the builtin value");
        }
        finally
        {
            File.Delete(builtinPath);
        }
    }

    [Test]
    public void RecursiveOptions_OrderedAfterUserDefinedOptions_RegardlessOfRegistrationOrder()
    {
        // Recursive options (--verbosity, --output) must appear AFTER the user's
        // locally declared options on every command — including the RootCommand,
        // where the verbosity/output wiring is typically applied before command
        // discovery. The System.CommandLine built-ins (--help, --version) are
        // outside of our control and ignored here.
        var builder = Tool.CreateBuilder([])
            .UseSerilog()
            .UseVerbosityOptions()
            .UseObjectOutput()
            .AddCommandsFromAssembly(typeof(RootWithLocalOption).Assembly);

        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        var infoIndex = names.IndexOf("--info");
        Assert.That(infoIndex, Is.GreaterThanOrEqualTo(0), "The local --info option should be registered on the root command.");

        foreach (var recursiveOption in new[] { "--verbosity", "-v", "-q", "--output" })
        {
            var idx = names.IndexOf(recursiveOption);
            Assert.That(idx, Is.GreaterThan(infoIndex),
                $"Recursive option {recursiveOption} must appear after the local --info option.");
        }
    }
}

[Command(Description = "Root-level command with a local option, for ordering tests.")]
public class RootWithLocalOption
{
    [Option("--info")]
    public bool Info { get; set; }

    public Task ExecuteAsync() => Task.CompletedTask;
}
