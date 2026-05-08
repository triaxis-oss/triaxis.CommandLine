namespace triaxis.CommandLine.Tests;

using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using triaxis.CommandLine.SourceGenerator;

[TestFixture]
public class ConfigureServicesGeneratorTests
{
    private static readonly MetadataReference[] s_baseReferences = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        // Reference the whole set of trusted platform assemblies so the test
        // compilation can bind BCL types, Microsoft.Extensions.*, etc.
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

    private static string? RunGeneratorAndGetEntryPoint(string userSource, OutputKind outputKind = OutputKind.ConsoleApplication)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(userSource);
        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: s_baseReferences,
            options: new CSharpCompilationOptions(outputKind));

        var runResult = CSharpGeneratorDriver
            .Create(new CommandTreeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _)
            .GetRunResult();

        foreach (var result in runResult.Results)
        {
            foreach (var generated in result.GeneratedSources)
            {
                if (generated.HintName == "GeneratedProgram.g.cs")
                {
                    return generated.SourceText.ToString();
                }
            }
        }
        return null;
    }

    [Test]
    public void Generator_EmitsConfigureServicesCall_WhenStaticMethodIsMarked()
    {
        const string source = """
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
            }

            public static class Startup
            {
                [ConfigureServices]
                public static void Register(IServiceCollection services) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null, "entry point was not generated");
        Assert.That(entryPoint, Does.Contain(".ConfigureServices(global::Startup.Register)"));
    }

    [Test]
    public void Generator_EmitsMultipleConfigureServicesCalls_InStableOrder()
    {
        const string source = """
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
            }

            namespace Alpha
            {
                public static class Startup
                {
                    [ConfigureServices]
                    public static void Register(IServiceCollection services) { }
                }
            }

            namespace Beta
            {
                public static class Startup
                {
                    [ConfigureServices]
                    public static void Register(IServiceCollection services) { }
                }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        var alphaIndex = entryPoint!.IndexOf(".ConfigureServices(global::Alpha.Startup.Register)", StringComparison.Ordinal);
        var betaIndex = entryPoint!.IndexOf(".ConfigureServices(global::Beta.Startup.Register)", StringComparison.Ordinal);
        Assert.That(alphaIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(betaIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(alphaIndex, Is.LessThan(betaIndex), "hooks should be emitted in ordinal order by declaring type FQN");
    }

    [Test]
    public void Generator_SkipsNonStaticMethods()
    {
        const string source = """
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
            }

            public class Startup
            {
                [ConfigureServices]
                public void Register(IServiceCollection services) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Not.Contain(".ConfigureServices("),
            "non-static methods must be ignored");
    }

    [Test]
    public void Generator_SkipsMethodsWithWrongSignature()
    {
        const string source = """
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
            }

            public static class Startup
            {
                [ConfigureServices]
                public static void RegisterZero() { }

                [ConfigureServices]
                public static void RegisterTwo(IServiceCollection services, string extra) { }

                [ConfigureServices]
                public static int RegisterNonVoid(IServiceCollection services) => 0;
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Not.Contain(".ConfigureServices("),
            "methods with incompatible shapes must be ignored");
    }

    [Test]
    public void Generator_EmitsConfigureServices_WithoutToolPackage_FallbackPath()
    {
        // No LoggingCommand type referenced ⇒ generator takes the minimal fallback
        // (AddCommandsFromAssembly + Run). It must still invoke registered hooks.
        const string source = """
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
            }

            public static class Startup
            {
                [ConfigureServices]
                public static void Register(IServiceCollection services) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".AddCommandsFromAssembly("));
        Assert.That(entryPoint, Does.Contain(".ConfigureServices(global::Startup.Register)"));
        Assert.That(entryPoint, Does.Contain(".Run()"));
    }

    [Test]
    public void Generator_DoesNotEmitEntryPoint_WhenNotAConsoleApp()
    {
        const string source = """
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            [Command("greet")]
            public class GreetCommand
            {
                public void Execute() { }
            }

            public static class Startup
            {
                [ConfigureServices]
                public static void Register(IServiceCollection services) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source, OutputKind.DynamicallyLinkedLibrary);
        Assert.That(entryPoint, Is.Null, "entry point must not be emitted for non-console projects");
    }
}
