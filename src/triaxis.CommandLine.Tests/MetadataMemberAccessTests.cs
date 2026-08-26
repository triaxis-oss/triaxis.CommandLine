namespace triaxis.CommandLine.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using triaxis.CommandLine.SourceGenerator;

/// <summary>
/// Members of an <c>[Options]</c> type that lives in a referenced assembly. The generator
/// decides how to reach a member by inspecting its declaration, and a symbol from metadata
/// has none — so every such decision needs a metadata answer. Getting one wrong is
/// invisible until run time: the emitted accessor names a field that was never emitted and
/// binding dies with <c>MissingFieldException</c>.
/// </summary>
[TestFixture]
public class MetadataMemberAccessTests
{
    private const string OptionsLibrary = """
        using triaxis.CommandLine;

        namespace Contracts;

        public class TailOptions
        {
            private string? _target;
            private int _limit;

            [Option("--target")]
            public string? Target { get => _target; init => _target = value?.Trim(); }

            // Protected, because that is as non-public as a member can get and still be
            // visible here: MetadataImportOptions.Public — what every real build uses —
            // imports only public and protected members from a referenced assembly.
            [Option("--limit")]
            protected int Limit { get => _limit; set => _limit = value < 0 ? 0 : value; }

            [Option("--follow")]
            public bool Follow { get; init; }

            public int LimitValue => _limit;
        }
        """;

    private const string Consumer = """
        using triaxis.CommandLine;
        using Contracts;

        [Command("tail")]
        public class TailCommand
        {
            [Options]
            public TailOptions Options { get; set; } = new();

            public void Execute() { }
        }
        """;

    private static (string? Tree, Compilation Updated) RunGenerator()
    {
        var reference = GeneratorTestCompilation.CreateReference(OptionsLibrary, "Contracts");
        var compilation = GeneratorTestCompilation.Create(Consumer, extraReferences: reference);

        var runResult = CSharpGeneratorDriver
            .Create(new CommandTreeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _)
            .GetRunResult();

        var tree = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Where(s => s.HintName == "GeneratedCommandTree.g.cs")
            .Select(s => s.SourceText.ToString())
            .FirstOrDefault();

        return (tree, updated);
    }

    [Test]
    public void ComputedProperties_BindThroughTheirAccessors_NotABackingField()
    {
        var (tree, updated) = RunGenerator();

        Assert.That(tree, Is.Not.Null);
        // A computed accessor has no backing field on either side of the assembly
        // boundary. The generator used to assume every property it could not inspect was
        // an auto-property, so these went to `<Name>k__BackingField`.
        Assert.That(tree, Does.Not.Contain("k__BackingField"));
        Assert.That(tree, Does.Not.Contain("GetField("));

        // Which accessor shape carries that depends on the compilation, not on the
        // property: the generator picks UnsafeAccessor or reflection once per compilation
        // and the net48 leg has no UnsafeAccessor. Asserting one shape passes on three
        // legs and fails on the fourth.
        if (updated.GetTypeByMetadataName("System.Runtime.CompilerServices.UnsafeAccessorAttribute") is not null)
        {
            Assert.That(tree, Does.Contain("Name = \"set_Target\""));
            Assert.That(tree, Does.Contain("Name = \"get_Limit\""));
            Assert.That(tree, Does.Contain("Name = \"set_Limit\""));
        }
        else
        {
            Assert.That(tree, Does.Contain("GetProperty(\"Target\""));
            Assert.That(tree, Does.Contain("GetProperty(\"Limit\""));
        }
    }

    [Test]
    public void GeneratedSourceCompilesCleanly()
    {
        var (_, updated) = RunGenerator();

        var errors = updated.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS5001")
            .ToArray();

        Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(d => "  " + d)));
    }
}
