namespace triaxis.CommandLine.Tests;

using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using triaxis.CommandLine.SourceGenerator;

/// <summary>
/// Emission coverage for the general <c>[Configure]</c> entry-point hook. It accepts any
/// combination of <c>IToolBuilder</c> / <c>IHostBuilder</c> / <c>IServiceCollection</c>
/// (the same shapes a per-command <c>Configure</c> allows) and is folded into the
/// source-generated <c>Main</c>.
/// </summary>
[TestFixture]
public class ConfigureHookGeneratorTests
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
        // Reference the assemblies the generator must resolve explicitly: their
        // presence in the runner's TPA set is not reliable on every OS, and when
        // one is missing the corresponding [Configure] parameter classification
        // silently falls through to None and the generator emits the no-hooks
        // fallback (notably the IHostBuilder signature on the Windows runner).
        refs.Add(MetadataReference.CreateFromFile(typeof(CommandAttribute).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Hosting.IHostBuilder).Assembly.Location));
        try
        {
            refs.Add(MetadataReference.CreateFromFile(Assembly.Load("netstandard").Location));
        }
        catch (System.IO.FileNotFoundException)
        {
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

    private const string GreetCommand = """
        [Command("greet")]
        public class GreetCommand
        {
            public void Execute() { }
        }
        """;

    [Test]
    public void Generator_EmitsConfigureHook_ForServiceCollectionSignature()
    {
        var source = $$"""
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            {{GreetCommand}}

            public static class Startup
            {
                [Configure]
                public static void Setup(IServiceCollection services) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".Configure(b => b.ConfigureServices(s => global::Startup.Setup(s)))"));
    }

    [Test]
    public void Generator_EmitsConfigureHook_ForToolBuilderSignature()
    {
        var source = $$"""
            using triaxis.CommandLine;

            {{GreetCommand}}

            public static class Startup
            {
                [Configure]
                public static void Setup(IToolBuilder builder) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".Configure(b => global::Startup.Setup(b))"));
    }

    [Test]
    public void Generator_EmitsConfigureHook_ForHostBuilderSignature()
    {
        var source = $$"""
            using Microsoft.Extensions.Hosting;
            using triaxis.CommandLine;

            {{GreetCommand}}

            public static class Startup
            {
                [Configure]
                public static void Setup(IHostBuilder host) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".Configure(b => global::Startup.Setup(b))"));
    }

    [Test]
    public void Generator_EmitsConfigureHook_ForBuilderAndServicesSignature()
    {
        var source = $$"""
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            {{GreetCommand}}

            public static class Startup
            {
                [Configure]
                public static void Setup(IToolBuilder builder, IServiceCollection services) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".Configure(b => b.ConfigureServices(s => global::Startup.Setup(b, s)))"));
    }

    [Test]
    public void Generator_EmitsConfigureHook_ForNoParameters()
    {
        var source = $$"""
            using triaxis.CommandLine;

            {{GreetCommand}}

            public static class Startup
            {
                [Configure]
                public static void Setup() { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".Configure(b => global::Startup.Setup())"));
    }

    [Test]
    public void Generator_SkipsNonStaticConfigureHook()
    {
        var source = $$"""
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            {{GreetCommand}}

            public class Startup
            {
                [Configure]
                public void Setup(IServiceCollection services) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Not.Contain(".Configure("),
            "instance methods cannot be a global hook and must be ignored");
    }

    [Test]
    public void Generator_SkipsConfigureHook_WithUnsupportedOrDuplicateParam()
    {
        var source = $$"""
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            {{GreetCommand}}

            public static class Startup
            {
                [Configure]
                public static void Unsupported(string nope) { }

                [Configure]
                public static void Duplicate(IServiceCollection a, IServiceCollection b) { }

                [Configure]
                public static int NonVoid(IToolBuilder builder) => 0;
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Not.Contain(".Configure("),
            "unsupported parameter types, duplicates, and non-void returns must be ignored");
    }

    [Test]
    public void Generator_EmitsConfigureHooks_InStableOrder()
    {
        var source = $$"""
            using triaxis.CommandLine;

            {{GreetCommand}}

            namespace Alpha
            {
                public static class Startup
                {
                    [Configure]
                    public static void Setup(IToolBuilder builder) { }
                }
            }

            namespace Beta
            {
                public static class Startup
                {
                    [Configure]
                    public static void Setup(IToolBuilder builder) { }
                }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        var alphaIndex = entryPoint!.IndexOf("global::Alpha.Startup.Setup(b)", StringComparison.Ordinal);
        var betaIndex = entryPoint!.IndexOf("global::Beta.Startup.Setup(b)", StringComparison.Ordinal);
        Assert.That(alphaIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(betaIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(alphaIndex, Is.LessThan(betaIndex),
            "hooks must be emitted in ordinal order by declaring type FQN");
    }

    [Test]
    public void Generator_EmitsConfigureHook_FallbackPath_WithoutToolPackage()
    {
        var source = $$"""
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            {{GreetCommand}}

            public static class Startup
            {
                [Configure]
                public static void Setup(IServiceCollection services) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".AddCommandsFromAssembly("));
        Assert.That(entryPoint, Does.Contain(".Configure(b => b.ConfigureServices(s => global::Startup.Setup(s)))"));
        Assert.That(entryPoint, Does.Contain(".Run()"));
    }

    // Presence of triaxis.CommandLine.LoggingCommand is how the generator detects the
    // Tool meta-package; a stub type flips it onto the opinionated-stack code path.
    private const string ToolMarker = """
        namespace triaxis.CommandLine { public sealed class LoggingCommand { } }
        """;

    // A non-void/-int return type makes the generator emit UseObjectOutput.
    private const string OutputCommand = """
        [Command("report")]
        public class ReportCommand
        {
            public string Execute() => "x";
        }
        """;

    [Test]
    public void Generator_ConfigureServicesOnly_WithToolPackage_KeepsDefaultStack()
    {
        var source = $$"""
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            {{GreetCommand}}
            {{ToolMarker}}

            public static class Startup
            {
                [ConfigureServices]
                public static void Register(IServiceCollection services) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".UseSerilog()"));
        Assert.That(entryPoint, Does.Contain(".UseVerbosityOptions()"));
        Assert.That(entryPoint, Does.Contain(".UseDefaultConfiguration("));
        Assert.That(entryPoint, Does.Contain(".ConfigureServices(global::Startup.Register)"),
            "[ConfigureServices] alone must not suppress the opinionated default stack");
    }

    [Test]
    public void Generator_ConfigureHook_WithToolPackage_SuppressesDefaultStack()
    {
        var source = $$"""
            using triaxis.CommandLine;

            {{GreetCommand}}
            {{ToolMarker}}

            public static class Startup
            {
                [Configure]
                public static void Setup(IToolBuilder builder) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".AddCommandsFromAssembly("),
            "command discovery still runs even when the default stack is suppressed");
        Assert.That(entryPoint, Does.Contain(".Configure(b => global::Startup.Setup(b))"));
        Assert.That(entryPoint, Does.Not.Contain(".UseSerilog()"),
            "a [Configure] hook owns logging setup, so the logging helpers are dropped");
        Assert.That(entryPoint, Does.Not.Contain(".UseVerbosityOptions()"));
        Assert.That(entryPoint, Does.Not.Contain(".UseDefaultConfiguration("));
    }

    [Test]
    public void Generator_ConfigureHook_WithToolPackage_StillEmitsUseObjectOutput()
    {
        var source = $$"""
            using triaxis.CommandLine;

            {{OutputCommand}}
            {{ToolMarker}}

            public static class Startup
            {
                [Configure]
                public static void Setup(IToolBuilder builder) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".AddCommandsFromAssembly("));
        Assert.That(entryPoint, Does.Contain(".UseObjectOutput()"),
            "UseObjectOutput is structural (driven by command return types) and stays even when a [Configure] hook suppresses the opinionated stack");
        Assert.That(entryPoint, Does.Contain(".Configure(b => global::Startup.Setup(b))"));
        Assert.That(entryPoint, Does.Not.Contain(".UseSerilog()"));
        Assert.That(entryPoint, Does.Not.Contain(".UseDefaultConfiguration("));
    }

    [Test]
    public void Generator_ConfigureAndServicesHooks_WithToolPackage_SuppressStack_EmitBoth()
    {
        var source = $$"""
            using Microsoft.Extensions.DependencyInjection;
            using triaxis.CommandLine;

            {{GreetCommand}}
            {{ToolMarker}}

            public static class Startup
            {
                [ConfigureServices]
                public static void Register(IServiceCollection services) { }

                [Configure]
                public static void Setup(IToolBuilder builder) { }
            }
            """;

        var entryPoint = RunGeneratorAndGetEntryPoint(source);
        Assert.That(entryPoint, Is.Not.Null);
        Assert.That(entryPoint, Does.Contain(".AddCommandsFromAssembly("));
        Assert.That(entryPoint, Does.Contain(".ConfigureServices(global::Startup.Register)"));
        Assert.That(entryPoint, Does.Contain(".Configure(b => global::Startup.Setup(b))"));
        Assert.That(entryPoint, Does.Not.Contain(".UseSerilog()"),
            "any [Configure] hook suppresses the default stack, even alongside [ConfigureServices]");
        Assert.That(entryPoint, Does.Not.Contain(".UseDefaultConfiguration("));
    }
}
