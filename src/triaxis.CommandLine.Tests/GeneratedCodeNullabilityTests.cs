namespace triaxis.CommandLine.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using triaxis.CommandLine.SourceGenerator;

/// <summary>A nullable warning in generated code is one the consumer can neither fix nor suppress.</summary>
[TestFixture]
public class GeneratedCodeNullabilityTests
{
    private static void AssertCompilesCleanly(string source, params MetadataReference[] references)
    {
        var compilation = GeneratorTestCompilation.Create(source, nullable: true, extraReferences: references);

        CSharpGeneratorDriver
            .Create(new CommandTreeGenerator(), new ObjectDescriptorGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var errors = updated.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS5001")
            .ToArray();

        Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(d => "  " + d)));
    }

    /// <summary>Behind the metadata boundary an init-only property binds through <c>set_</c>, whose parameter rejects <c>T?</c>.</summary>
    [Test]
    public void OptionsRecordFromAnotherAssembly()
    {
        var library = GeneratorTestCompilation.CreateReference("""
            using triaxis.CommandLine;

            namespace Contracts;

            public record DiffOptions
            {
                [Option("--app", Required = true)]
                public required string App { get; init; }

                [Option("--format")]
                public string Format { get; init; } = "table";

                [Option("--note")]
                public string? Note { get; init; }
            }
            """, "Contracts", nullable: true);

        AssertCompilesCleanly("""
            using triaxis.CommandLine;
            using Contracts;

            [Command("diff")]
            public class DiffCommand
            {
                [Options] private readonly DiffOptions _options = null!;

                public void Execute() { }
            }
            """, library);
    }

    [Test]
    public void CommandMembers()
    {
        AssertCompilesCleanly("""
            using System.Collections.Generic;
            using triaxis.CommandLine;

            public class SharedOptions
            {
                [Option("--region")]
                public required string Region { get; init; }

                [Option("--profile")]
                public string Profile { get; set; } = "default";

                [Option("--label")]
                private string _label = "";

                [Option("--tag")]
                protected List<string> Tags { get; set; } = [];

                private string _mode = "fast";

                [Option("--mode")]
                protected string Mode { get => _mode; set => _mode = value; }

                [Option("--comment")]
                public string? Comment { get; set; }
            }

            [Command("deploy")]
            public class DeployCommand
            {
                [Argument("target")]
                public required string Target { get; init; }

                [Argument("extra")]
                private string[] _extra = [];

                [Option("--app")]
                public required string App { get; init; }

                [Option("--format")]
                private string Format { get; set; } = "table";

                [Option("--count")]
                public int Count { get; set; }

                [Option("--note")]
                public string? Note { get; set; }

                [Options]
                public SharedOptions Shared { get; set; } = null!;

                [Inject]
                public required System.IServiceProvider Services { get; init; }

                [Inject]
                private System.IServiceProvider _provider = null!;

                public void Execute() { }
            }
            """);
    }

    [Test]
    public void AnnotatedTypeArgumentsSurvive()
    {
        AssertCompilesCleanly("""
            using System.Collections.Generic;
            using triaxis.CommandLine;

            public record Resource(string Name, IReadOnlyDictionary<string, object?> Properties);

            [Command("list")]
            public class ListCommand
            {
                public IEnumerable<Resource> Execute() => [];
            }
            """);
    }

    [Test]
    public void Descriptors()
    {
        AssertCompilesCleanly("""
            using System.Collections.Generic;
            using triaxis.CommandLine;

            public record Address(string City, string? Street);

            public record Person(string Name, string? Nickname, Address? Home, List<string?> Aliases);

            public class Page<T>
            {
                public List<T> Items { get; set; } = [];
            }

            [Command("people")]
            public class PeopleCommand
            {
                public IEnumerable<Person?> Execute() => [];
            }

            [Command("pairs")]
            public class PairsCommand
            {
                public IEnumerable<(string Name, List<string?> Tags, Address? Home)> Execute() => [];
            }

            [Command("pages")]
            public class PagesCommand
            {
                public Page<Address?> Execute() => new();
            }
            """);
    }
}
