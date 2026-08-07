namespace triaxis.CommandLine.Tests;

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Roslyn setup shared by every source-generator test fixture.
///
/// Getting the reference set wrong here fails silently, which is what makes it
/// dangerous: an unbound attribute produces no error, it just yields empty
/// <c>ConstructorArguments</c>/<c>NamedArguments</c>, so the generator sees a command
/// with no path and no aliases and the test then asserts against that. The fixtures
/// used to keep four hand-picked lists, and a hand-picked list cannot cover .NET
/// Framework, where netstandard's type forwards fan out across mscorlib, System,
/// System.Core and the façades. So each host contributes its whole framework — by
/// directory where that works, by loaded assembly for what sits in the GAC instead —
/// and <see cref="Create"/> refuses to hand back a compilation that didn't bind.
/// </summary>
static class GeneratorTestCompilation
{
    private static readonly MetadataReference[] s_references = BuildReferences();

    /// <summary>
    /// <c>init</c> accessors, <c>required</c> members and <c>[ModuleInitializer]</c> need
    /// compiler-recognised marker types that .NET Framework's mscorlib doesn't carry.
    /// Generated code uses all three, so without these the net48 leg binds them
    /// differently from every other leg — the same silent divergence this class exists to
    /// prevent. A real .NET Framework consumer supplies them the same way, via PolySharp;
    /// this repo's own test projects do exactly that.
    /// </summary>
    private const string ModernMemberPolyfill = """
        namespace System.Runtime.CompilerServices
        {
            internal static class IsExternalInit { }

            [AttributeUsage(AttributeTargets.Method, Inherited = false)]
            internal sealed class ModuleInitializerAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field
                | AttributeTargets.Property, Inherited = false)]
            internal sealed class RequiredMemberAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
            internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
            {
                public string FeatureName { get; } = featureName;
            }
        }
        """;

    /// <remarks>
    /// The probe has to require a <em>public</em> definition. Several netstandard
    /// libraries — System.CommandLine among them — embed their own <c>internal</c> copies
    /// of these markers, which a metadata-name lookup happily returns even though test
    /// source cannot use them. Treating one of those as "already available" silently
    /// disables the polyfill and every fixture then fails to compile.
    /// </remarks>
    private static readonly bool s_needsModernMemberPolyfill =
        !CSharpCompilation.Create("probe", references: s_references)
            .GetTypesByMetadataName("System.Runtime.CompilerServices.IsExternalInit")
            .Any(t => t.DeclaredAccessibility == Accessibility.Public);

    /// <summary>
    /// A broken reference set breaks every fixture at once, and a test-runner log only
    /// reports the count — the reasons go to a result file nobody reads until they have
    /// to. Compile one canonical command once and put any errors on stderr, so the
    /// reason travels with the failure.
    /// </summary>
    private static readonly bool s_canaryReported = ReportCanary();

    private static bool ReportCanary()
    {
        var canary = CSharpCompilation.Create(
            "canary",
            [CSharpSyntaxTree.ParseText("""
                using triaxis.CommandLine;
                [Command("probe", Aliases = ["p"])]
                public class ProbeCommand { public void Execute() { } }
                """)],
            s_references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        var errors = canary.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS5001")
            .ToArray();
        if (errors.Length > 0)
        {
            Console.Error.WriteLine($"[{nameof(GeneratorTestCompilation)}] reference set is broken "
                + $"on this host ({RuntimeInformation.FrameworkDescription}); every generator test "
                + $"will fail:{Environment.NewLine}"
                + string.Join(Environment.NewLine, errors.Select(d => "  " + d)));
            Console.Error.Flush();
        }
        return errors.Length == 0;
    }

    private static MetadataReference[] BuildReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (tpa is not null)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                AddIfManaged(path);
            }
        }
        else
        {
            // .NET Framework and mono publish no TPA. Reference the whole framework
            // directory plus Facades rather than naming assemblies one by one: which
            // ones a source needs isn't predictable, because netstandard's type forwards
            // fan out across mscorlib, System, System.Core and the façades.
            //
            // The directory has to come from the runtime, not from mscorlib's own
            // Location: .NET Framework loads mscorlib out of the GAC, so that path leads
            // to a folder holding mscorlib and nothing else. Mono happens to report the
            // framework directory for both, which is why it never showed the difference.
            var frameworkDir = RuntimeEnvironment.GetRuntimeDirectory();
            AddDirectory(frameworkDir);
            AddDirectory(Path.Combine(frameworkDir, "Facades"));
        }

        // Loaded assemblies, for everything a directory scan can miss: on .NET Framework
        // mscorlib and the netstandard façade both live in the GAC rather than in the
        // framework directory, and triaxis.CommandLine targets netstandard, so without
        // the façade to follow its type forwards none of it binds at all.
        AddIfManaged(typeof(object).Assembly.Location);
        AddIfManaged(typeof(Task).Assembly.Location);
        AddIfManaged(typeof(CommandAttribute).Assembly.Location);
        // What the *generated* code binds against, as opposed to the test source: the
        // command tree needs System.CommandLine and descriptors implement IPropertyGetter.
        // On modern hosts TPA already carries both; .NET Framework has no TPA, so they
        // have to be named or the generator tests compile against a reference set no real
        // consumer has — and only a test that compiles generated output ever notices.
        AddIfManaged(typeof(System.CommandLine.ParseResult).Assembly.Location);
        AddIfManaged(typeof(triaxis.Reflection.IPropertyGetter).Assembly.Location);
        AddIfManaged(typeof(IServiceCollection).Assembly.Location);
        AddIfManaged(typeof(IHostBuilder).Assembly.Location);
        try
        {
            AddIfManaged(Assembly.Load("netstandard").Location);
        }
        catch (System.IO.FileNotFoundException)
        {
            // Modern .NET hosts already carry it via TPA.
        }
        return refs.ToArray();

        void AddDirectory(string dir)
        {
            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.GetFiles(dir, "*.dll"))
                {
                    AddIfManaged(path);
                }
            }
        }

        void AddIfManaged(string path)
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            try
            {
                // Two references with one identity is an error on every compilation, and
                // the same assembly genuinely turns up twice: .NET Framework serves
                // mscorlib and netstandard from the GAC while the framework directory
                // holds its own copies. First one wins — the framework directory is
                // scanned before the GAC-resident additions.
                if (AssemblyName.GetAssemblyName(path).Name is not { } name || !seen.Add(name))
                {
                    return;
                }
                refs.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception)
            {
                // Native or otherwise unreadable image sitting in the same directory.
            }
        }
    }

    /// <summary>
    /// Builds the test compilation and fails the test if the source doesn't compile
    /// cleanly. Test sources are fragments, so the only tolerated error is the missing
    /// entry point; anything else means the generator is being fed a degraded
    /// compilation, and whatever the test asserts about it is worthless.
    /// </summary>
    internal static CSharpCompilation Create(string source, string assemblyName = "TestAssembly",
        OutputKind outputKind = OutputKind.ConsoleApplication)
    {
        _ = s_canaryReported;
        SyntaxTree[] trees = s_needsModernMemberPolyfill
            ? [CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(ModernMemberPolyfill)]
            : [CSharpSyntaxTree.ParseText(source)];

        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: trees,
            references: s_references,
            options: new CSharpCompilationOptions(outputKind));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS5001")
            .ToArray();
        if (errors.Length > 0)
        {
            Assert.Fail("The generator test compilation does not compile cleanly, so the generator "
                + $"would silently see incomplete data instead of failing:{Environment.NewLine}"
                + string.Join(Environment.NewLine, errors.Select(d => "  " + d)));
        }

        return compilation;
    }
}
