namespace triaxis.CommandLine.Tests;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using triaxis.CommandLine.SourceGenerator;

/// <summary>
/// A command name is matched against a single command-line token, so one with whitespace
/// in it can never be typed. The generator reports it as <c>TXCL008</c> — most often for
/// <c>[Command("group sub")]</c>, written as a path but taken as one name.
/// </summary>
[TestFixture]
public class UnusableCommandNameTests
{
    private static ImmutableArray<Diagnostic> RunGenerator(string userSource)
    {
        var compilation = GeneratorTestCompilation.Create(userSource, "mytool", OutputKind.ConsoleApplication);

        CSharpGeneratorDriver
            .Create(new CommandTreeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return diagnostics;
    }

    [Test]
    public void ReportsTXCL008_WhenAPathSegmentContainsWhitespace()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("group sub")]
            public class GroupSubCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL008"), Is.True);
    }

    [Test]
    public void ReportsTXCL008_WhenASegmentIsEmpty()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("group", "")]
            public class GroupCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL008"), Is.True);
    }

    [Test]
    public void ReportsTXCL008_WhenAnAliasContainsWhitespace()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("group", Aliases = ["g rp"])]
            public class GroupCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL008"), Is.True);
    }

    [Test]
    public void ReportsTXCL008_ForAnAssemblyLevelCommand()
    {
        const string source = """
            using triaxis.CommandLine;

            [assembly: Command("group sub", Description = "a group")]

            [Command("other")]
            public class OtherCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL008"), Is.True);
    }

    [Test]
    public void AcceptsOrdinaryNames()
    {
        const string source = """
            using triaxis.CommandLine;

            [Command("group", "sub-command", Aliases = ["sc"])]
            public class GroupSubCommand
            {
                public void Execute() { }
            }
            """;

        Assert.That(RunGenerator(source).Any(d => d.Id == "TXCL008"), Is.False);
    }

    /// <summary>
    /// The umbrella class name is derived from the path, so a name holding characters no
    /// identifier can must still emit compilable C# — otherwise a single bad name buries
    /// its own diagnostic under a wall of syntax errors in generated code.
    /// </summary>
    [TestCase("group sub", TestName = "GeneratedSourceCompiles_WithWhitespaceInTheName")]
    [TestCase("dot.ted", TestName = "GeneratedSourceCompiles_WithAPunctuatedName")]
    [TestCase("7zip", TestName = "GeneratedSourceCompiles_WithALeadingDigit")]
    public void GeneratedSourceCompiles(string name)
    {
        var compilation = GeneratorTestCompilation.Create($$"""
            using triaxis.CommandLine;

            [Command("{{name}}")]
            public class OddlyNamedCommand
            {
                [Argument]
                public string? Value { get; set; }

                public void Execute() { }
            }
            """, "mytool", OutputKind.ConsoleApplication);

        CSharpGeneratorDriver
            .Create(new CommandTreeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var errors = updated.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS5001")
            .ToArray();

        Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(d => "  " + d)));
    }
}
