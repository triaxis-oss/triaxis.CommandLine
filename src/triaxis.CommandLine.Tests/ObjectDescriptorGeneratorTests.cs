namespace triaxis.CommandLine.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using triaxis.CommandLine.SourceGenerator;

[TestFixture]
public class ObjectDescriptorGeneratorTests
{
    private static string? RunGenerator(string userSource)
    {
        var compilation = GeneratorTestCompilation.Create(userSource);

        var runResult = CSharpGeneratorDriver
            .Create(new ObjectDescriptorGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _)
            .GetRunResult();

        foreach (var result in runResult.Results)
        {
            foreach (var generated in result.GeneratedSources)
            {
                if (generated.HintName == "ObjectDescriptors.g.cs")
                {
                    return generated.SourceText.ToString();
                }
            }
        }

        return null;
    }

    private const string Forecast = """
        using System.Collections.Generic;
        using triaxis.CommandLine;
        using triaxis.CommandLine.ObjectOutput;

        public record Forecast(string City, decimal Temperature);

        [Command("forecast")]
        public class ForecastCommand
        {
            public IEnumerable<Forecast> Execute() => [];
        }
        """;

    /// <summary>
    /// Asserting on the emitted text only proves it says the right words. Feeding it back
    /// through the compiler is what proves it is valid C# that binds against the real
    /// interfaces — the failure mode this generator has is emitting something plausible
    /// that does not compile in the consumer.
    /// </summary>
    [Test]
    public void GeneratedSourceCompilesCleanly()
    {
        var compilation = GeneratorTestCompilation.Create(Forecast);

        // Both generators, because that is what a consumer gets — and because the
        // ModuleInitializerAttribute polyfill the descriptors rely on comes from the other
        // one. Running this generator alone would test a compilation nobody has.
        CSharpGeneratorDriver
            .Create(new ObjectDescriptorGenerator(), new CommandTreeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var generatorDiagnostics);

        Assert.That(generatorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);

        var errors = updated.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS5001")
            .ToArray();

        Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(d => "  " + d)));
    }

    [Test]
    public void DescribesTheOutputElementType()
    {
        var source = RunGenerator(Forecast);

        Assert.That(source, Is.Not.Null, "no descriptor source was generated");
        Assert.That(source, Does.Contain("if (type == typeof(global::Forecast))"));
        Assert.That(source, Does.Contain("public string Name => \"City\";"));
        Assert.That(source, Does.Contain("public string Name => \"Temperature\";"));
        Assert.That(source, Does.Contain("((global::Forecast)target).City"));
        Assert.That(source, Does.Contain("[ModuleInitializer]"));
        Assert.That(source, Does.Contain("ObjectDescriptorRegistry.Register(TryGet)"));
    }

    [Test]
    public void GeneratesNothing_WhenNoCommandProducesOutput()
    {
        var source = RunGenerator("""
            using triaxis.CommandLine;

            [Command("noop")]
            public class NoopCommand
            {
                public void Execute() { }
            }
            """);

        Assert.That(source, Is.Null);
    }

    [Test]
    public void DescribesNestedTypesTransitively()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;

            public record Endpoint(string Host, int Port);
            public record Probe(string Path);
            public record Service(string Name, Endpoint Listen, List<Probe> Probes);

            [Command("svc")]
            public class ServiceCommand
            {
                public IEnumerable<Service> Execute() => [];
            }
            """);

        Assert.That(source, Is.Not.Null);
        Assert.That(source, Does.Contain("typeof(global::Service)"));
        Assert.That(source, Does.Contain("typeof(global::Endpoint)"), "a nested member type must be described too");
        Assert.That(source, Does.Contain("typeof(global::Probe)"), "a collection member's element type must be described too");
    }

    [Test]
    public void ResolvesFieldOrderAtGenerationTime()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;
            using triaxis.CommandLine.ObjectOutput;

            public class Row
            {
                public string First { get; set; } = "";
                public string Last { get; set; } = "";
                [ObjectOutput(Before = nameof(First))] public string Zeroth { get; set; } = "";
            }

            [Command("rows")]
            public class RowsCommand
            {
                public IEnumerable<Row> Execute() => [];
            }
            """);

        Assert.That(source, Is.Not.Null);

        var order = new[] { "\"Zeroth\"", "\"First\"", "\"Last\"" }
            .Select(n => source!.IndexOf(n, StringComparison.Ordinal))
            .ToArray();

        Assert.That(order, Is.All.GreaterThanOrEqualTo(0), "every field must be emitted");
        Assert.That(order, Is.Ordered, "Before = First must place Zeroth ahead of it in the emitted array");
    }

    [Test]
    public void CarriesVisibilityAndFormat()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;
            using triaxis.CommandLine.ObjectOutput;

            public class Row
            {
                [ObjectOutput(Format = "F1")] public double Value { get; set; }
                [ObjectOutput(ObjectFieldVisibility.Extended)] public string Detail { get; set; } = "";
            }

            [Command("rows")]
            public class RowsCommand
            {
                public IEnumerable<Row> Execute() => [];
            }
            """);

        Assert.That(source, Is.Not.Null);
        Assert.That(source, Does.Contain("public string? Format => \"F1\";"));
        Assert.That(source, Does.Contain("ObjectFieldVisibility.Extended;"));
    }

    [Test]
    public void SurvivesASelfReferencingGraph()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;

            public class Node
            {
                public string Name { get; set; } = "";
                public Node? Parent { get; set; }
            }

            [Command("tree")]
            public class TreeCommand
            {
                public IEnumerable<Node> Execute() => [];
            }
            """);

        Assert.That(source, Is.Not.Null);
        Assert.That(source, Does.Contain("typeof(global::Node)"));
    }

    [Test]
    public void FlattensTupleElements()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;
            using triaxis.CommandLine.ObjectOutput;

            public record Forecast(string City, decimal Temperature);

            public class Extension
            {
                [ObjectOutput(After = nameof(Forecast.City))] public string ReverseCity => "";
                [ObjectOutput(After = nameof(Forecast.Temperature))] public decimal Negative => 0;
            }

            [Command("t")]
            public class TupleCommand
            {
                public IEnumerable<(Forecast, Extension)> Execute() => [];
            }
            """);

        Assert.That(source, Is.Not.Null);
        Assert.That(source, Does.Contain(".Item1.City"), "an element's members are lifted onto the tuple");
        Assert.That(source, Does.Contain(".Item2.ReverseCity"));

        // ordering anchors have to reach across elements, which is the whole point of
        // flattening rather than nesting
        var order = new[] { "\"City\"", "\"ReverseCity\"", "\"Temperature\"", "\"Negative\"" }
            .Select(n => source!.IndexOf(n, StringComparison.Ordinal))
            .ToArray();

        Assert.That(order, Is.All.GreaterThanOrEqualTo(0));
        Assert.That(order, Is.Ordered, "After anchors must interleave the two elements");
    }

    [Test]
    public void ScalarTupleElementBecomesOneNamedField()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;

            [Command("t")]
            public class TupleCommand
            {
                public IEnumerable<(string City, int Count)> Execute() => [];
            }
            """);

        Assert.That(source, Is.Not.Null, "a tuple of scalars is still describable");
        Assert.That(source, Does.Contain("public string Name => \"City\";"));
        Assert.That(source, Does.Contain("public string Name => \"Count\";"));
        Assert.That(source, Does.Contain(").City;"), "named elements are accessed by their declared name");
        Assert.That(source, Does.Not.Contain("\"Length\""),
            "the reflective descriptor turns this into string.Length; the generated one must not");
    }

    [Test]
    public void FlattensNestedTuples()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;

            public record Inner(string Deep);

            [Command("t")]
            public class TupleCommand
            {
                public IEnumerable<(string Top, (Inner Nested, int N) Sub)> Execute() => [];
            }
            """);

        Assert.That(source, Is.Not.Null);
        Assert.That(source, Does.Contain(".Sub.Nested.Deep"));
        Assert.That(source, Does.Contain(".Sub.N"));
    }

    [Test]
    public void SkipsTypesWithNoDescribableMembers()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;

            public class Empty { }

            [Command("e")]
            public class EmptyCommand
            {
                public IEnumerable<Empty> Execute() => [];
            }
            """);

        Assert.That(source, Is.Null, "an empty descriptor is worse than deferring to the fallback");
    }
    /// <summary>
    /// Resolving a handler used to close <c>IObjectOutputHandler&lt;T&gt;</c> with
    /// <c>MakeGenericType</c> against an open-generic registration, which NativeAOT cannot
    /// do for a value type — so every struct output type, and therefore every tuple,
    /// failed at run time. The generator closes them instead.
    /// </summary>
    [Test]
    public void EmitsClosedHandlerRegistrations_WhenObjectOutputIsReferenced()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;

            public record struct Reading(string Sensor, int Value);

            [Command("readings")]
            public class ReadingsCommand
            {
                public IEnumerable<Reading> Execute() => [];
            }
            """);

        Assert.That(source, Is.Not.Null);
        Assert.That(source, Does.Contain("ObjectOutputHandlerRegistry.Register(AddHandlers, TryGetHandler)"));
        Assert.That(source, Does.Contain("services.TryAddTransient<IObjectOutputHandler<global::Reading>, DefaultObjectOutputHandler<global::Reading>>();"));
        Assert.That(source, Does.Contain("services.TryAddSingleton<IObjectDescriptorProvider<global::Reading>, DefaultObjectDescriptorProvider<global::Reading>>();"),
            "the handler takes IObjectDescriptorProvider<T>, so that generic has to be closed too");
        Assert.That(source, Does.Contain("return services.GetService<IObjectOutputHandler<global::Reading>>();"));
    }

    [Test]
    public void OmitsHiddenFields()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;
            using triaxis.CommandLine.ObjectOutput;

            public class Row
            {
                public string Shown { get; set; } = "";
                [ObjectOutput(ObjectFieldVisibility.Internal)] public string Internal { get; set; } = "";
                [ObjectOutput(ObjectFieldVisibility.Hidden)] public string Secret { get; set; } = "";
            }

            [Command("rows")]
            public class RowsCommand
            {
                public IEnumerable<Row> Execute() => [];
            }
            """);

        Assert.That(source, Is.Not.Null);
        Assert.That(source, Does.Contain("\"Shown\""));
        Assert.That(source, Does.Contain("\"Internal\""), "Internal stays — it is hidden from tables, not from the data formats");
        Assert.That(source, Does.Not.Contain("\"Secret\""), "Hidden must not reach the descriptor at all");
        Assert.That(source, Does.Not.Contain(".Secret;"));
    }

    [Test]
    public void GeneratesNothing_WhenEveryFieldIsHidden()
    {
        var source = RunGenerator("""
            using System.Collections.Generic;
            using triaxis.CommandLine;
            using triaxis.CommandLine.ObjectOutput;

            public class Row
            {
                [ObjectOutput(ObjectFieldVisibility.Hidden)] public string Secret { get; set; } = "";
            }

            [Command("rows")]
            public class RowsCommand
            {
                public IEnumerable<Row> Execute() => [];
            }
            """);

        Assert.That(source, Is.Null, "an all-hidden type leaves no fields, so no descriptor is emitted");
    }
}
