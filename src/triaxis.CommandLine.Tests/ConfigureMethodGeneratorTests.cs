namespace triaxis.CommandLine.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using triaxis.CommandLine.SourceGenerator;

/// <summary>
/// Verifies the source generator's detection of an optional static <c>Configure</c>
/// method on <c>[Command]</c>-attributed types. The detection accepts any combination
/// of <see cref="IToolBuilder"/>, <see cref="IHostBuilder"/>, and
/// <see cref="IServiceCollection"/> parameters in any order; other shapes are silently
/// ignored. Detection materializes as an <c>ICommandConfigurator</c> implementation on
/// the generated action class so the hook fires only when its command is invoked.
/// </summary>
[TestFixture]
public class ConfigureMethodGeneratorTests
{
    private static readonly MetadataReference[] s_baseReferences = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var refs = new List<MetadataReference>();
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (tpa is not null)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    refs.Add(MetadataReference.CreateFromFile(path));
                }
            }
        }
        // .NET Framework hosts don't expose TRUSTED_PLATFORM_ASSEMBLIES, so without
        // these the Roslyn compilation can't resolve `Task` and the generator's
        // `MainAsync` detection silently falls through to the regular Execute path.
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Task).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(CommandAttribute).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(IHostBuilder).Assembly.Location));
        return refs.ToArray();
    }

    private static string? RunGeneratorAndGetCommandTree(string userSource)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(userSource);
        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: s_baseReferences,
            options: new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        var runResult = CSharpGeneratorDriver
            .Create(new CommandTreeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _)
            .GetRunResult();

        foreach (var result in runResult.Results)
        {
            foreach (var generated in result.GeneratedSources)
            {
                if (generated.HintName == "GeneratedCommandTree.g.cs")
                {
                    return generated.SourceText.ToString();
                }
            }
        }
        return null;
    }

    [Test]
    public void Generator_ImplementsICommandConfigurator_WhenCommandHasParameterlessConfigure()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public static void Configure() { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        Assert.That(tree, Does.Contain("ICommandConfigurator"));
        Assert.That(tree, Does.Contain("global::GreetCommand.Configure();"));
    }

    [Test]
    public void Generator_ImplementsICommandConfigurator_WhenCommandHasIToolBuilderConfigure()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public static void Configure(IToolBuilder builder) { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        Assert.That(tree, Does.Contain("AsynchronousCommandLineAction, global::triaxis.CommandLine.ICommandConfigurator"));
        Assert.That(tree, Does.Contain("public void Configure(global::triaxis.CommandLine.IToolBuilder builder)"));
        Assert.That(tree, Does.Contain("global::GreetCommand.Configure(builder);"));
    }

    [Test]
    public void Generator_ImplementsICommandConfigurator_WhenCommandHasIHostBuilderConfigure()
    {
        const string source = """
            using Microsoft.Extensions.Hosting;
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public static void Configure(IHostBuilder hostBuilder) { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        Assert.That(tree, Does.Contain("ICommandConfigurator"));
        Assert.That(tree, Does.Contain("global::GreetCommand.Configure(builder);"));
    }

    [Test]
    public void Generator_ImplementsICommandConfigurator_WhenCommandHasIServiceCollectionConfigure()
    {
        const string source = """
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public static void Configure(IServiceCollection services) { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        // For a single-IServiceCollection signature the generator routes through
        // IToolBuilder.ConfigureServices so the user receives the live ServiceCollection.
        Assert.That(tree, Does.Contain("builder.ConfigureServices(services => global::GreetCommand.Configure(services))"));
    }

    [Test]
    public void Generator_PreservesParameterOrder_WhenAllThreeParamsArePresent()
    {
        const string source = """
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public static void Configure(IServiceCollection services, IToolBuilder builder, IHostBuilder hostBuilder) { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        // Parameter order is preserved so the user's declared positions still bind correctly.
        Assert.That(tree, Does.Contain(
            "builder.ConfigureServices(services => global::GreetCommand.Configure(services, builder, builder))"));
    }

    [Test]
    public void Generator_DoesNotImplementICommandConfigurator_WhenNoConfigureDeclared()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        Assert.That(tree, Does.Not.Contain("ICommandConfigurator"));
    }

    [Test]
    public void Generator_ImplementsICommandConfigurator_OnStandaloneCommandToo()
    {
        const string source = """
            using System.Threading.Tasks;
            using triaxis.CommandLine;

            [Command("standalone")]
            public class StandaloneCommand
            {
                public Task MainAsync() => Task.CompletedTask;
                public static void Configure(IToolBuilder builder) { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        Assert.That(tree, Does.Contain("IStandaloneAction, global::triaxis.CommandLine.ICommandConfigurator"));
        Assert.That(tree, Does.Contain("global::StandaloneCommand.Configure(builder);"));
    }

    [Test]
    public void Generator_SkipsConfigure_WhenInstanceMethod()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public void Configure(IToolBuilder builder) { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        Assert.That(tree, Does.Not.Contain("ICommandConfigurator"),
            "non-static Configure must not register a configurator");
    }

    [Test]
    public void Generator_SkipsConfigure_WhenNonVoidReturn()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public static int Configure(IToolBuilder builder) => 0;
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        Assert.That(tree, Does.Not.Contain("ICommandConfigurator"));
    }

    [Test]
    public void Generator_SkipsConfigure_WhenUnsupportedParameterType()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public static void Configure(IToolBuilder builder, string extra) { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        Assert.That(tree, Does.Not.Contain("ICommandConfigurator"));
    }

    [Test]
    public void Generator_SkipsConfigure_WhenDuplicateParameterTypes()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public static void Configure(IToolBuilder a, IToolBuilder b) { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        Assert.That(tree, Does.Not.Contain("ICommandConfigurator"));
    }
}
