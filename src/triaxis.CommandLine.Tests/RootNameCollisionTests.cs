namespace triaxis.CommandLine.Tests;

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using triaxis.CommandLine.SourceGenerator;

/// <summary>
/// System.CommandLine names the root command after the executable, so a top-level
/// command reusing that name makes the tokenizer throw "An item with the same key has
/// already been added" at parse time. The generator reports it as <c>TXCL007</c>.
/// </summary>
[TestFixture]
public class RootNameCollisionTests
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
        // .NET Framework hosts expose no TRUSTED_PLATFORM_ASSEMBLIES, so mscorlib has to
        // come in explicitly — without it `string` doesn't bind, the `params string[]`
        // constructor of [Command] goes unresolved, and the attribute's arguments come
        // back empty (which reads here as "no path and no aliases", silently passing the
        // negative tests and failing the positive ones).
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Task).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(CommandAttribute).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location));
        // See ConfigureMethodGeneratorTests.BuildReferences — without netstandard the
        // .NET Framework host can't fully bind triaxis.CommandLine's types.
        try
        {
            refs.Add(MetadataReference.CreateFromFile(Assembly.Load("netstandard").Location));
        }
        catch (System.IO.FileNotFoundException)
        {
            // No netstandard available — modern .NET hosts already covered via TPA.
        }
        return refs.ToArray();
    }

    private static ImmutableArray<Diagnostic> RunGenerator(string userSource,
        string assemblyName = "mytool", OutputKind outputKind = OutputKind.ConsoleApplication)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(userSource);
        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [syntaxTree],
            references: s_baseReferences,
            options: new CSharpCompilationOptions(outputKind));

        CSharpGeneratorDriver
            .Create(new CommandTreeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return diagnostics;
    }

    [Test]
    public void Generator_ReportsTXCL007_WhenCommandNameMatchesExecutable()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("mytool")]
            public class ToolCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL007"), Is.True);
    }

    [Test]
    public void Generator_ReportsTXCL007_WhenCommandGroupMatchesExecutable()
    {
        // Only the first segment reaches the root level, so a nested path still collides.
        const string source = """
            using triaxis.CommandLine;

            [Command("mytool", "sub")]
            public class ToolCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL007"), Is.True);
    }

    [Test]
    public void Generator_ReportsTXCL007_WhenTopLevelAliasMatchesExecutable()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("other", Aliases = ["mytool"])]
            public class ToolCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL007"), Is.True);
    }

    [Test]
    public void Generator_ReportsTXCL007_WhenAssemblyLevelCommandMatchesExecutable()
    {
        const string source = """
            using triaxis.CommandLine;

            [assembly: Command("mytool", Description = "group")]

            [Command("mytool", "sub")]
            public class ToolCommand
            {
                public void Execute() { }
            }
            """;

        var diags = RunGenerator(source).Where(d => d.Id == "TXCL007").ToArray();
        Assert.That(diags.Any(d => d.GetMessage().Contains("Assembly-level command 'mytool'")), Is.True);
    }

    [Test]
    public void Generator_DoesNotReportTXCL007_ForRootCommand()
    {
        // The documented fix: no path at all makes the class the root command.
        const string source = """
            using triaxis.CommandLine;

            [Command]
            public class ToolCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL007"), Is.False);
    }

    [Test]
    public void Generator_DoesNotReportTXCL007_ForNestedCommandOrDifferentCasing()
    {
        // Nested names live in their own token map, and the tokenizer matches ordinally.
        const string source = """
            using triaxis.CommandLine;

            [Command("group", "mytool")]
            public class NestedCommand
            {
                public void Execute() { }
            }

            [Command("MyTool")]
            public class CasedCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL007"), Is.False);
    }

    [Test]
    public void Generator_DoesNotReportTXCL007_ForLibraries()
    {
        // A library's assembly name is not the name of whatever executable ends up
        // hosting its commands, so the check would be a false positive there.
        const string source = """
            using triaxis.CommandLine;

            [Command("mytool")]
            public class ToolCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source, outputKind: OutputKind.DynamicallyLinkedLibrary)
            .Any(d => d.Id == "TXCL007"), Is.False);
    }
}
