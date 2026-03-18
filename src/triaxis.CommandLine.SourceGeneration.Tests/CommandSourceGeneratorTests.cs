using triaxis.CommandLine.SourceGeneration;

namespace triaxis.CommandLine.SourceGeneration.Tests;

/// <summary>
/// Tests for the <see cref="CommandSourceGenerator"/>.
/// Verifies that the generated code contains the expected registration logic
/// for commands, arguments, options, and DI registrations.
/// </summary>
[TestFixture]
public class CommandSourceGeneratorTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the source generator against the given source code snippets and
    /// returns the generated source text (if any).
    /// </summary>
    private static IReadOnlyList<string> RunGenerator(params string[] sources)
    {
        // Build a compilation with the attribute assemblies in scope
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        // Ensure the triaxis.CommandLine.Abstractions assembly is referenced
        var abstractionsRef = MetadataReference.CreateFromFile(
            typeof(CommandAttribute).Assembly.Location);
        if (!references.Any(r => r.Display == abstractionsRef.Display))
            references.Add(abstractionsRef);

        var syntaxTrees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        var generator = new CommandSourceGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);

        var result = driver.GetRunResult();
        return result.GeneratedTrees
            .Select(t => t.GetText().ToString())
            .ToList();
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    [Test]
    public void NoCommandTypes_ProducesNoOutput()
    {
        var generated = RunGenerator("class Foo {}");
        Assert.That(generated, Is.Empty);
    }

    [Test]
    public void SimpleCommand_GeneratesAddGeneratedCommandsMethod()
    {
        var source = """
            using triaxis.CommandLine;

            [Command("hello", Description = "Says hello")]
            public class HelloCommand
            {
                public void Execute() { }
            }
            """;

        var generated = RunGenerator(source);

        Assert.That(generated, Has.Count.EqualTo(1));
        var code = generated[0];

        // Should contain the extension method
        Assert.That(code, Does.Contain("AddGeneratedCommands"));
        Assert.That(code, Does.Contain("namespace triaxis.CommandLine"));
        Assert.That(code, Does.Contain("internal static partial class CommandLineSetup"));
    }

    [Test]
    public void SimpleCommand_RegistersInDI()
    {
        var source = """
            using triaxis.CommandLine;

            namespace MyApp;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
            }
            """;

        var generated = RunGenerator(source);

        Assert.That(generated, Has.Count.EqualTo(1));
        var code = generated[0];

        Assert.That(code, Does.Contain("AddTransient<global::MyApp.GreetCommand>"));
    }

    [Test]
    public void SimpleCommand_SetsDescriptionAndPath()
    {
        var source = """
            using triaxis.CommandLine;

            [Command("tool", "run", Description = "Runs the tool")]
            public class RunCommand
            {
                public void Execute() { }
            }
            """;

        var generated = RunGenerator(source);

        Assert.That(generated, Has.Count.EqualTo(1));
        var code = generated[0];

        // Path should be passed to GetCommand
        Assert.That(code, Does.Contain("GetCommand(\"tool\", \"run\")"));
        Assert.That(code, Does.Contain("Description = \"Runs the tool\""));
    }

    [Test]
    public void CommandWithArgument_GeneratesArgumentBinding()
    {
        var source = """
            using triaxis.CommandLine;

            [Command("hello")]
            public class HelloCommand
            {
                [Argument("--name", Description = "Name")]
                public string Name { get; set; } = "";

                public void Execute() { }
            }
            """;

        var generated = RunGenerator(source);

        Assert.That(generated, Has.Count.EqualTo(1));
        var code = generated[0];

        // Should create an Argument<T>
        Assert.That(code, Does.Contain("System.CommandLine.Argument<"));
        Assert.That(code, Does.Contain("--name"));
        Assert.That(code, Does.Contain("Description = \"Name\""));
        // Public property → direct access (no SetValue)
        Assert.That(code, Does.Contain(".Name ="));
    }

    [Test]
    public void CommandWithPrivateField_UsesReflectionForBinding()
    {
        var source = """
            using triaxis.CommandLine;

            [Command("cmd")]
            public class MyCommand
            {
                [Argument]
                private string _target = "";

                public void Execute() { }
            }
            """;

        var generated = RunGenerator(source);

        Assert.That(generated, Has.Count.EqualTo(1));
        var code = generated[0];

        // Private field → reflection-based access
        Assert.That(code, Does.Contain("GetField(\"_target\""));
        Assert.That(code, Does.Contain(".SetValue("));
    }

    [Test]
    public void CommandWithOption_GeneratesOptionBinding()
    {
        var source = """
            using triaxis.CommandLine;

            [Command("cmd")]
            public class MyCommand
            {
                [Option("--verbose", Description = "Enable verbose output")]
                public bool Verbose { get; set; }

                public void Execute() { }
            }
            """;

        var generated = RunGenerator(source);

        Assert.That(generated, Has.Count.EqualTo(1));
        var code = generated[0];

        Assert.That(code, Does.Contain("System.CommandLine.Option<"));
        Assert.That(code, Does.Contain("--verbose"));
        // Public property → direct access
        Assert.That(code, Does.Contain(".Verbose ="));
    }

    [Test]
    public void MultipleCommands_AllRegistered()
    {
        var source = """
            using triaxis.CommandLine;

            [Command("cmd1")]
            public class Command1 { public void Execute() {} }

            [Command("cmd2")]
            public class Command2 { public void Execute() {} }
            """;

        var generated = RunGenerator(source);

        Assert.That(generated, Has.Count.EqualTo(1));
        var code = generated[0];

        Assert.That(code, Does.Contain("AddTransient<global::Command1>"));
        Assert.That(code, Does.Contain("AddTransient<global::Command2>"));
        Assert.That(code, Does.Contain("GetCommand(\"cmd1\")"));
        Assert.That(code, Does.Contain("GetCommand(\"cmd2\")"));
    }

    [Test]
    public void GeneratedCode_IsInTriaxisCommandLineNamespace()
    {
        var source = """
            using triaxis.CommandLine;

            [Command("x")]
            public class XCommand { public void Execute() {} }
            """;

        var generated = RunGenerator(source);

        Assert.That(generated, Has.Count.EqualTo(1));
        // Extension method class lives in triaxis.CommandLine so it's automatically in scope
        Assert.That(generated[0], Does.Contain("namespace triaxis.CommandLine"));
    }

    [Test]
    public void GeneratedCode_UsesGeneratedCommandHandler()
    {
        var source = """
            using triaxis.CommandLine;

            [Command("x")]
            public class XCommand { public void Execute() {} }
            """;

        var generated = RunGenerator(source);

        Assert.That(generated, Has.Count.EqualTo(1));
        Assert.That(generated[0], Does.Contain("GeneratedCommandHandler"));
    }
}
