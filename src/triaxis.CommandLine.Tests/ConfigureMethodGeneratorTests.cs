namespace triaxis.CommandLine.Tests;

using System.Collections.Immutable;
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
        Assert.That(tree, Does.Contain("public void Configure(global::triaxis.CommandLine.IToolBuilder builder, ParseResult parseResult)"));
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
    public void Generator_ImplementsICommandConfigurator_WhenCommandHasInstanceConfigure()
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
        Assert.That(tree, Does.Contain("ICommandConfigurator"));
        // Instance Configure: the action class constructs and binds the command
        // before invoking the user's instance method, then stashes the instance for
        // reuse at InvokeAsync.
        Assert.That(tree, Does.Contain("private global::GreetCommand? _configuredInstance;"),
            "instance Configure should stash the configure-phase instance");
        Assert.That(tree, Does.Contain("instance.Configure(builder)"),
            "instance Configure should be dispatched on the constructed instance");
        Assert.That(tree, Does.Contain("_configuredInstance = instance;"));
    }

    [Test]
    public void Generator_PrefersInstanceConfigure_WhenBothShapesPresent()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
                public void Configure(IToolBuilder builder) { }
                public static void Configure() { }
            }
            """;

        var tree = RunGeneratorAndGetCommandTree(source);
        Assert.That(tree, Is.Not.Null);
        // The instance shape wins because it's the more capable form (sees bound values).
        Assert.That(tree, Does.Contain("instance.Configure(builder)"));
        Assert.That(tree, Does.Not.Contain("global::GreetCommand.Configure();"),
            "the static fallback must not be wired up when an instance shape is present");
    }

    [Test]
    public void Generator_ReportsTXCL006_WhenConfigureReturnsNonVoid()
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

        var diags = RunGeneratorAndGetDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TXCL006"), Is.True,
            "a Configure method that returns non-void should be flagged rather than silently dropped");
    }

    [Test]
    public void Generator_ReportsTXCL006_WhenConfigureHasUnsupportedParameterType()
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

        var diags = RunGeneratorAndGetDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TXCL006"), Is.True,
            "a Configure method with a foreign parameter type should be flagged");
    }

    [Test]
    public void Generator_ReportsTXCL006_AndPreservesTXCL004_WhenInstanceConfigureIsUnusableAndCtorTakesParameters()
    {
        // Regression: a method named Configure with a foreign parameter shape used to
        // be silently dropped, which also suppressed TXCL004 since cmd.ConfigureMethod
        // ended up null. Now both diagnostics fire so the user sees the real problem.
        const string source = """
            using triaxis.CommandLine;

            public interface IDep { }
            public interface IFoo { }

            [Command("greet")]
            public class GreetCommand(IDep dep)
            {
                public void Execute() { _ = dep; }
                public void Configure(IFoo foo) { _ = foo; }
            }
            """;

        var diags = RunGeneratorAndGetDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TXCL006"), Is.True,
            "the unrecognised Configure shape should be flagged as TXCL006");
    }

    private static ImmutableArray<Diagnostic> RunGeneratorAndGetDiagnostics(string userSource)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(userSource);
        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: s_baseReferences,
            options: new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        CSharpGeneratorDriver
            .Create(new CommandTreeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return diagnostics;
    }

    [Test]
    public void Generator_ReportsTXCL004_WhenInstanceConfigureMeetsCtorParameters()
    {
        // Instance Configure is constructed without DI (the provider doesn't exist yet),
        // so a constructor that needs DI is unsatisfiable at Configure time.
        const string source = """
            using triaxis.CommandLine;

            public interface IDep { }

            [Command("greet")]
            public class GreetCommand(IDep dep)
            {
                public void Execute() { }
                public void Configure(IToolBuilder builder) { }
            }
            """;

        var diags = RunGeneratorAndGetDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TXCL004"), Is.True,
            "instance Configure with ctor parameters should produce TXCL004");
    }

    [Test]
    public void Generator_ReportsTXCL005_WhenInstanceConfigureMeetsRequiredInject()
    {
        const string source = """
            using triaxis.CommandLine;

            public interface IDep { }

            [Command("greet")]
            public class GreetCommand
            {
                [Inject] public required IDep Dep { get; init; }
                public void Execute() { }
                public void Configure(IToolBuilder builder) { }
            }
            """;

        var diags = RunGeneratorAndGetDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TXCL005"), Is.True,
            "instance Configure with required [Inject] should produce TXCL005");
    }

    [Test]
    public void Generator_DoesNotReportInstanceConfigureDiagnostics_WhenConfigureIsStatic()
    {
        const string source = """
            using triaxis.CommandLine;

            public interface IDep { }

            [Command("greet")]
            public class GreetCommand(IDep dep)
            {
                [Inject] public required IDep AlsoDep { get; init; }
                public void Execute() { }
                public static void Configure(IToolBuilder builder) { }
            }
            """;

        var diags = RunGeneratorAndGetDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TXCL004"), Is.False);
        Assert.That(diags.Any(d => d.Id == "TXCL005"), Is.False);
    }

    [Test]
    public void Generator_AllowsInstanceConfigure_WithNonRequiredInject()
    {
        // Non-required [Inject] is fine — the generator's InjectServices step assigns
        // it after Configure has run, before Execute.
        const string source = """
            using triaxis.CommandLine;

            public interface IDep { }

            [Command("greet")]
            public class GreetCommand
            {
                [Inject] public IDep Dep { get; set; } = null!;
                public void Execute() { }
                public void Configure(IToolBuilder builder) { }
            }
            """;

        var diags = RunGeneratorAndGetDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TXCL004"), Is.False);
        Assert.That(diags.Any(d => d.Id == "TXCL005"), Is.False);
    }

    [Test]
    public void Generator_ReportsTXCL006_WhenConfigureHasDuplicateParameterTypes()
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

        var diags = RunGeneratorAndGetDiagnostics(source);
        Assert.That(diags.Any(d => d.Id == "TXCL006"), Is.True,
            "a Configure method with duplicate parameter types should be flagged");
    }
}
