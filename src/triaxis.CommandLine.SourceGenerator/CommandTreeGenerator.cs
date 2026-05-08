using System.CodeDom.Compiler;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace triaxis.CommandLine.SourceGenerator;

[Generator]
public class CommandTreeGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var commandClasses = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "triaxis.CommandLine.CommandAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractCommandModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        var collected = commandClasses.Collect();
        var compilationInfo = context.CompilationProvider.Select(static (c, _) =>
        {
            var hasUnsafeAccessor = c.GetTypeByMetadataName("System.Runtime.CompilerServices.UnsafeAccessorAttribute") is not null;
            // OperatingSystem.IsOSPlatform(string) was added in .NET 5; when unavailable
            // the generator falls back to RuntimeInformation.IsOSPlatform(OSPlatform).
            var hasOperatingSystemIsOSPlatform = c.GetTypeByMetadataName("System.OperatingSystem")?
                .GetMembers("IsOSPlatform")
                .OfType<IMethodSymbol>()
                .Any(m => m.IsStatic && m.Parameters.Length == 1
                    && m.Parameters[0].Type.SpecialType == SpecialType.System_String) ?? false;
            var assemblyCommands = ExtractAssemblyCommands(c);
            return (AssemblyName: c.AssemblyName ?? "",
                    HasUnsafeAccessor: hasUnsafeAccessor,
                    HasOperatingSystemIsOSPlatform: hasOperatingSystemIsOSPlatform,
                    AssemblyCommands: assemblyCommands);
        });
        var combined = collected.Combine(compilationInfo);

        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var (commands, info) = pair;
            if (commands.IsDefaultOrEmpty && info.AssemblyCommands.IsDefaultOrEmpty)
            {
                return;
            }

            // Surface any validation diagnostics recorded during model extraction.
            // Commands with diagnostics are still passed through to GenerateSource so
            // the tree keeps a node for them (improving IDE experience), but the code
            // generator skips emitting their *_Action bodies.
            foreach (var command in commands.Where(c => !c.Diagnostics.IsDefaultOrEmpty))
            {
                foreach (var diag in command.Diagnostics)
                {
                    var colon = diag.IndexOf(':');
                    var id = colon > 0 ? diag.Substring(0, colon) : "TXCL000";
                    var message = colon > 0 ? diag.Substring(colon + 1) : diag;
                    var descriptor = new DiagnosticDescriptor(id, "Command generator validation", message,
                        category: "triaxis.CommandLine", DiagnosticSeverity.Error, isEnabledByDefault: true);
                    spc.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None));
                }
            }

            var source = GenerateSource(commands, info.AssemblyCommands, info.AssemblyName,
                info.HasUnsafeAccessor, info.HasOperatingSystemIsOSPlatform);
            spc.AddSource("GeneratedCommandTree.g.cs", source);
        });

        // Entry-point generation pipeline: when the consuming project is an executable
        // with no user-written Main method, synthesize a Program.Main that bootstraps
        // the tool.
        var entryPointProps = context.AnalyzerConfigOptionsProvider.Select(static (opts, _) =>
        {
            opts.GlobalOptions.TryGetValue("build_property.TriaxisCommandLineConfigOverridePath", out var configPath);
            opts.GlobalOptions.TryGetValue("build_property.TriaxisCommandLineEnvironmentVariablePrefix", out var envPrefix);

            return (ConfigOverridePath: string.IsNullOrEmpty(configPath) ? (string?)null : configPath,
                    EnvironmentVariablePrefix: string.IsNullOrEmpty(envPrefix) ? (string?)null : envPrefix);
        });

        // Discover static methods marked with [ConfigureServices] so the generated
        // entry point can invoke them as service-registration hooks without the user
        // having to hand-write a Main just to call .ConfigureServices().
        var configureServicesHooks = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "triaxis.CommandLine.ConfigureServicesAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => ExtractConfigureServicesHook(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .Collect();

        var entryPointModel = context.CompilationProvider
            .Combine(entryPointProps)
            .Combine(collected)
            .Combine(configureServicesHooks)
            .Select(static (pair, ct) =>
            {
                var (((compilation, props), commands), hooks) = pair;
                if (compilation.Options.OutputKind != OutputKind.ConsoleApplication)
                {
                    return null;
                }
                if (compilation.GetEntryPoint(ct) is not null)
                {
                    return null;
                }

                // Tool package (UseDefaults) detection via a type unique to it.
                var hasToolPackage = compilation.GetTypeByMetadataName("triaxis.CommandLine.LoggingCommand") is not null;

                // Object-output emission is conditional on at least one command actually
                // producing a value for the formatter to consume. Void/Task/int/Task<int>
                // returns all go through exit-code handling only.
                var producesOutput = !commands.IsDefaultOrEmpty && commands.Any(c =>
                    c.ExecuteMethod.ReturnKind is not (
                        ReturnKind.Void or
                        ReturnKind.Task or
                        ReturnKind.Int or
                        ReturnKind.TaskOfInt));

                // Stable emission order so generator output is deterministic across
                // reorderings of source files.
                var orderedHooks = hooks.IsDefaultOrEmpty
                    ? ImmutableArray<ConfigureServicesHookModel>.Empty
                    : hooks
                        .OrderBy(h => h.DeclaringTypeFqn, StringComparer.Ordinal)
                        .ThenBy(h => h.MethodName, StringComparer.Ordinal)
                        .ToImmutableArray();

                return new EntryPointModel(hasToolPackage,
                    hasToolPackage ? props.ConfigOverridePath : null,
                    hasToolPackage ? props.EnvironmentVariablePrefix : null,
                    producesOutput,
                    orderedHooks);
            });

        context.RegisterSourceOutput(entryPointModel, static (spc, model) =>
        {
            if (model is null)
            {
                return;
            }
            spc.AddSource("GeneratedProgram.g.cs", GenerateEntryPointSource(model));
        });
    }

    private static string GenerateEntryPointSource(EntryPointModel model)
    {
        var sw = new StringWriter();
        var w = new IndentedTextWriter(sw);

        w.WriteLine("// <auto-generated/>");
        w.WriteLine("#nullable enable");
        w.WriteLine();
        w.WriteLine("using triaxis.CommandLine;");
        w.WriteLine();
        w.Block("namespace triaxis.CommandLine.Generated", () =>
        {
            w.WriteLine("[global::System.CodeDom.Compiler.GeneratedCode(\"triaxis.CommandLine.SourceGenerator\", \"1.0\")]");
            w.Block("internal static class GeneratedProgram", () =>
            {
                w.Block("internal static int Main(string[] args)", () =>
                {
                    if (model.HasToolPackage)
                    {
                        // Chain the individual helpers directly (instead of calling
                        // UseDefaults) so that UseObjectOutput — and with it the
                        // triaxis.CommandLine.ObjectOutput + YamlDotNet graph — is only
                        // referenced when a command actually produces output. That lets
                        // trimming drop the unused formatting stack entirely.
                        // Command discovery runs first so the recursive options added
                        // by UseVerbosityOptions / UseObjectOutput are appended *after*
                        // every local option in the root command's option list.
                        var configParts = new List<string>();
                        if (model.ConfigOverridePath is not null)
                        {
                            configParts.Add($"configOverridePath: {FormatString(model.ConfigOverridePath)}");
                        }
                        if (model.EnvironmentVariablePrefix is not null)
                        {
                            configParts.Add($"environmentVariablePrefix: {FormatString(model.EnvironmentVariablePrefix)}");
                        }
                        var configArgs = string.Join(", ", configParts);

                        w.WriteLine("return global::triaxis.CommandLine.Tool.CreateBuilder(args)");
                        w.Indent++;
                        w.WriteLine(".AddCommandsFromAssembly(typeof(GeneratedProgram).Assembly)");
                        w.WriteLine(".UseSerilog()");
                        w.WriteLine(".UseVerbosityOptions()");
                        if (model.ProducesOutput)
                        {
                            w.WriteLine(".UseObjectOutput()");
                        }
                        w.WriteLine($".UseDefaultConfiguration({configArgs})");
                        EmitConfigureServicesHooks(w, model.ConfigureServicesHooks);
                        w.WriteLine(".Run();");
                        w.Indent--;
                    }
                    else
                    {
                        if (model.ConfigureServicesHooks.IsDefaultOrEmpty)
                        {
                            w.WriteLine("return global::triaxis.CommandLine.Tool.CreateBuilder(args).AddCommandsFromAssembly(typeof(GeneratedProgram).Assembly).Run();");
                        }
                        else
                        {
                            w.WriteLine("return global::triaxis.CommandLine.Tool.CreateBuilder(args)");
                            w.Indent++;
                            w.WriteLine(".AddCommandsFromAssembly(typeof(GeneratedProgram).Assembly)");
                            EmitConfigureServicesHooks(w, model.ConfigureServicesHooks);
                            w.WriteLine(".Run();");
                            w.Indent--;
                        }
                    }
                });
            });
        });

        w.Flush();
        return sw.ToString();
    }

    /// <summary>
    /// Emits the call site that invokes the user's <c>Configure</c> method. Static methods
    /// are dispatched as <c>Type.Configure(...)</c>; instance methods as
    /// <c>instance.Configure(...)</c> against an in-scope local named <c>instance</c>.
    /// When <see cref="ConfigureParamKind.ServiceCollection"/> is among the parameters,
    /// the call is wrapped in <c>builder.ConfigureServices(services =&gt; …)</c> so the
    /// user receives the live service collection.
    /// </summary>
    private static void EmitConfigureInvocation(IndentedTextWriter w, ConfigureMethodModel method)
    {
        var args = method.Parameters.Select(static p => p switch
        {
            ConfigureParamKind.ToolBuilder => "builder",
            ConfigureParamKind.HostBuilder => "builder",
            ConfigureParamKind.ServiceCollection => "services",
            _ => throw new InvalidOperationException("unreachable"),
        }).ToArray();

        var receiver = method.IsStatic ? method.DeclaringTypeFqn : "instance";
        var call = $"{receiver}.Configure({string.Join(", ", args)})";

        if (method.Parameters.Contains(ConfigureParamKind.ServiceCollection))
        {
            w.WriteLine($"builder.ConfigureServices(services => {call});");
        }
        else
        {
            w.WriteLine($"{call};");
        }
    }

    private static void EmitConfigureServicesHooks(IndentedTextWriter w, ImmutableArray<ConfigureServicesHookModel> hooks)
    {
        if (hooks.IsDefaultOrEmpty)
        {
            return;
        }
        foreach (var hook in hooks)
        {
            w.WriteLine($".ConfigureServices({hook.DeclaringTypeFqn}.{hook.MethodName})");
        }
    }

    private static ConfigureServicesHookModel? ExtractConfigureServicesHook(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method)
        {
            return null;
        }

        // The generated Main calls the method as a delegate, so only static methods with
        // the expected signature are usable. Non-matching shapes are silently skipped —
        // accessibility problems surface as ordinary compile errors at the call site.
        if (!method.IsStatic)
        {
            return null;
        }

        if (method.Parameters.Length != 1)
        {
            return null;
        }

        if (method.Parameters[0].Type.ToDisplayString() != "Microsoft.Extensions.DependencyInjection.IServiceCollection")
        {
            return null;
        }

        if (method.ReturnType.SpecialType != SpecialType.System_Void)
        {
            return null;
        }

        return new ConfigureServicesHookModel(
            method.ContainingType.ToDisplayString(FqnFormat),
            method.Name);
    }

    private static ImmutableArray<AssemblyCommandModel> ExtractAssemblyCommands(Compilation compilation)
    {
        var builder = ImmutableArray.CreateBuilder<AssemblyCommandModel>();
        foreach (var attr in compilation.Assembly.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != "triaxis.CommandLine.CommandAttribute")
            {
                continue;
            }

            var path = attr.ConstructorArguments
                .SelectMany(a => a.Kind == TypedConstantKind.Array
                    ? a.Values.Select(v => v.Value?.ToString())
                    : new[] { a.Value?.ToString() })
                .Where(s => s is not null)
                .ToArray();

            if (path.Length == 0)
            {
                continue;
            }

            string? description = null;
            string[]? aliases = null;
            foreach (var named in attr.NamedArguments)
            {
                switch (named.Key)
                {
                    case "Description":
                        description = named.Value.Value?.ToString();
                        break;
                    case "Aliases":
                        aliases = named.Value.Values.Select(v => v.Value?.ToString()!).ToArray();
                        break;
                }
            }

            builder.Add(new AssemblyCommandModel(path!, description, aliases));
        }

        return builder.ToImmutable();
    }

    private static CommandModel? ExtractCommandModel(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var typeSymbol = (INamedTypeSymbol)ctx.TargetSymbol;

        var commandAttr = ctx.Attributes.FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "triaxis.CommandLine.CommandAttribute");

        if (commandAttr is null)
        {
            return null;
        }

        var path = commandAttr.ConstructorArguments
            .SelectMany(a => a.Kind == TypedConstantKind.Array
                ? a.Values.Select(v => v.Value?.ToString())
                : new[] { a.Value?.ToString() })
            .Where(s => s is not null)
            .ToArray();

        // Fall back to syntax-tree literals when semantic resolution didn't surface
        // any path arguments. This happens when the consuming compilation can't fully
        // bind the `params string[]` constructor — typically because a referenced
        // assembly's TFM-forwarded types aren't reachable. The attribute itself still
        // matches by name (so we got here), but `ConstructorArguments` comes back
        // empty. Reading the literals directly off the attribute syntax keeps the
        // path accurate regardless of binding completeness.
        if (path.Length == 0
            && commandAttr.ApplicationSyntaxReference?.GetSyntax(ct) is AttributeSyntax attrSyntax
            && attrSyntax.ArgumentList is { } argList)
        {
            path = argList.Arguments
                .Where(a => a.NameEquals is null)
                .Select(a => (a.Expression as LiteralExpressionSyntax)?.Token.ValueText)
                .Where(s => s is not null)
                .ToArray()!;
        }

        string? description = null;
        string[]? aliases = null;

        foreach (var named in commandAttr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Description":
                    description = named.Value.Value?.ToString();
                    break;
                case "Aliases":
                    aliases = named.Value.Values.Select(v => v.Value?.ToString()!).ToArray();
                    break;
            }
        }

        if (description is null)
        {
            var descAttr = typeSymbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute");
            description = descAttr?.ConstructorArguments.FirstOrDefault().Value?.ToString();
        }

        // Collect [SupportedOSPlatform("...")] attributes, walking the base-type chain
        // and stopping at the derived-most type that declares any — that type wins and
        // any base-class platform sets are hidden. Multiple attributes on that type
        // combine with a logical OR. The generator emits the equivalent
        // System.OperatingSystem check as CommandTreeNode.IsSupported so gated commands
        // only get registered on matching platforms.
        //
        // SupportedOSPlatformAttribute declares Inherited=false, but a base-class check
        // is what users intuitively expect (e.g. a WindowsServiceBase that's
        // "windows"-only), so we walk the hierarchy explicitly.
        string[]? supportedPlatforms = null;
        for (var current = typeSymbol; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() is "object" or "System.Object")
            {
                break;
            }
            var onThisType = current.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.Versioning.SupportedOSPlatformAttribute")
                .Select(a => a.ConstructorArguments.FirstOrDefault().Value?.ToString())
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToArray();
            if (onThisType.Length > 0)
            {
                supportedPlatforms = onThisType;
                break;
            }
        }

        var members = new List<MemberModel>();
        for (var current = typeSymbol; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() is "object" or "System.Object")
            {
                break;
            }
            CollectMembers(current, members, ct);
        }

        var (executeMethod, isStandalone) = DetectEntryPoint(typeSymbol);
        var ctorParams = ExtractConstructorParameters(typeSymbol);

        // Validate standalone commands: no [Inject], parameterless ctor, no co-existing
        // ExecuteAsync/Execute. Collect diagnostic messages here and surface them in the
        // RegisterSourceOutput callback (which has access to ReportDiagnostic).
        var diagBuilder = ImmutableArray.CreateBuilder<string>();
        // DetectConfigureMethod also pushes diagnostics for any method named Configure
        // whose signature isn't recognized — silently dropping such a method would mask
        // real mistakes (see TXCL006).
        var configureMethod = DetectConfigureMethod(typeSymbol, diagBuilder);
        var actionOptions = DetectActionOptions(typeSymbol);
        if (isStandalone)
        {
            var entryPointName = executeMethod.MethodName; // "Main" or "MainAsync"
            if (members.Any(m => m.Kind == MemberKind.Inject))
            {
                diagBuilder.Add($"TXCL001:Command '{typeSymbol.Name}' declares {entryPointName} so it runs without a service provider; [Inject] members are not supported on standalone commands.");
            }
            if (!ctorParams.IsEmpty)
            {
                diagBuilder.Add($"TXCL002:Command '{typeSymbol.Name}' declares {entryPointName} but has a constructor with parameters; standalone commands require a parameterless constructor.");
            }
            // Reject if the class also defines ExecuteAsync / Execute — the standalone
            // marker and the DI/middleware-routed marker are mutually exclusive, and
            // silently preferring the standalone one would be surprising.
            var hasExecute = typeSymbol.GetMembers("ExecuteAsync").OfType<IMethodSymbol>().Any(m => m.Parameters.Length <= 1)
                || typeSymbol.GetMembers("Execute").OfType<IMethodSymbol>().Any(m => m.Parameters.Length == 0);
            if (hasExecute)
            {
                diagBuilder.Add($"TXCL003:Command '{typeSymbol.Name}' declares both {entryPointName} and ExecuteAsync/Execute; pick one entry point.");
            }
        }
        // [ActionOption] methods are independent of the command's primary kind: a
        // builder-taking action option emits its own IStandaloneAction class even on
        // an otherwise DI-routed command (and vice versa), so no kind-match check is
        // needed here.

        // Instance Configure is constructed *before* the service provider exists, via a
        // parameterless ctor, so neither constructor parameters nor required [Inject]
        // members can be satisfied. Either of these combinations is rejected here so the
        // user picks one shape: switch Configure to static, or remove the ctor params /
        // required modifier.
        if (configureMethod is { IsStatic: false })
        {
            if (!ctorParams.IsEmpty)
            {
                diagBuilder.Add($"TXCL004:Command '{typeSymbol.Name}' declares an instance Configure method but its constructor takes parameters. Instance Configure runs before the service provider exists, so constructor injection is not available — make Configure static, or move the dependencies to [Inject] members.");
            }
            if (members.Any(m => m.Kind == MemberKind.Inject && m.NeedsInitializer))
            {
                diagBuilder.Add($"TXCL005:Command '{typeSymbol.Name}' declares an instance Configure method and a required [Inject] member. Instance Configure runs before the service provider exists, so 'required' [Inject] members cannot be satisfied — make Configure static, or drop the 'required' modifier so the inject happens after Configure.");
            }
        }
        var diagnostics = diagBuilder.ToImmutable();

        return new CommandModel(
            typeSymbol.ToDisplayString(FqnFormat),
            path!,
            description,
            aliases,
            supportedPlatforms,
            members.ToImmutableArray(),
            executeMethod,
            ctorParams,
            IsStandalone: isStandalone,
            ConfigureMethod: configureMethod,
            ActionOptions: actionOptions,
            Diagnostics: diagnostics);
    }

    private static ImmutableArray<ActionOptionModel> DetectActionOptions(INamedTypeSymbol typeSymbol)
    {
        var builder = ImmutableArray.CreateBuilder<ActionOptionModel>();
        // Walk the type and its base classes for [ActionOption] methods. Private members on
        // base types aren't callable from the generated sibling action class, so skip them.
        for (var current = typeSymbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            var isBase = !SymbolEqualityComparer.Default.Equals(current, typeSymbol);
            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.IsStatic || method.MethodKind != MethodKind.Ordinary)
                {
                    continue;
                }
                if (isBase && method.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }
                var attr = method.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "triaxis.CommandLine.ActionOptionAttribute");
                if (attr is null)
                {
                    continue;
                }

                // Recognised parameter shapes mirror MainAsync:
                //   ()
                //   (CancellationToken)
                //   (IToolBuilder)
                //   (IToolBuilder, CancellationToken)
                var acceptsToolBuilder = false;
                var acceptsCt = false;
                var shapeOk = method.Parameters.Length switch
                {
                    0 => true,
                    1 => IsCtParameter(method.Parameters[0], ref acceptsCt) || IsBuilderParameter(method.Parameters[0], ref acceptsToolBuilder),
                    2 => IsBuilderParameter(method.Parameters[0], ref acceptsToolBuilder) && IsCtParameter(method.Parameters[1], ref acceptsCt),
                    _ => false,
                };
                if (!shapeOk)
                {
                    continue;
                }

                var (kind, innerType) = AnalyzeReturnType(method.ReturnType);

                var name = GetNamedArgString(attr, "Name") ?? GetConstructorArgString(attr, 0);
                var description = GetNamedArgString(attr, "Description");
                var aliases = GetConstructorArgStringArray(attr, 1) ?? GetNamedArgStringArray(attr, "Aliases");
                var order = GetNamedArgDouble(attr, "Order");

                builder.Add(new ActionOptionModel(
                    method.Name,
                    name,
                    description,
                    aliases,
                    order,
                    acceptsToolBuilder,
                    acceptsCt,
                    method.ReturnType.ToDisplayString(FqnFormat),
                    kind,
                    innerType));
            }
        }
        return builder.ToImmutable();

        static bool IsCtParameter(IParameterSymbol p, ref bool accepts)
        {
            if (p.Type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                accepts = true;
                return true;
            }
            return false;
        }

        static bool IsBuilderParameter(IParameterSymbol p, ref bool accepts)
        {
            if (p.Type.ToDisplayString() == "triaxis.CommandLine.IToolBuilder")
            {
                accepts = true;
                return true;
            }
            return false;
        }
    }

    private static ConfigureMethodModel? DetectConfigureMethod(INamedTypeSymbol typeSymbol,
        ImmutableArray<string>.Builder diagnostics)
    {
        // Prefer instance Configure when both shapes are present — instance gives access
        // to bound [Argument]/[Option] values, which is the more capable form. The static
        // form remains supported for commands that need constructor DI (which can't coexist
        // with instance Configure; the validator below rejects that combination).
        IMethodSymbol? candidate = null;
        var staticFallback = (IMethodSymbol?)null;
        var rejected = new List<(IMethodSymbol Method, string Reason)>();
        foreach (var m in typeSymbol.GetMembers("Configure").OfType<IMethodSymbol>())
        {
            if (m.MethodKind != MethodKind.Ordinary)
            {
                continue;
            }

            if (m.ReturnType.SpecialType != SpecialType.System_Void)
            {
                rejected.Add((m, $"return type is '{m.ReturnType.ToDisplayString()}'; Configure must return 'void'"));
                continue;
            }

            var ok = true;
            string? reason = null;
            var seen = ConfigureParamKind.None;
            for (var i = 0; i < m.Parameters.Length; i++)
            {
                var p = m.Parameters[i];
                var kind = ClassifyConfigureParam(p.Type);
                if (kind == ConfigureParamKind.None)
                {
                    reason = $"parameter '{p.Name}' has type '{p.Type.ToDisplayString()}', which is not one of the supported parameter types (IToolBuilder, IHostBuilder, IServiceCollection)";
                    ok = false;
                    break;
                }
                if ((seen & kind) != 0)
                {
                    reason = $"parameter '{p.Name}' duplicates the '{p.Type.ToDisplayString()}' parameter; each supported type may appear at most once";
                    ok = false;
                    break;
                }
                seen |= kind;
            }
            if (!ok)
            {
                rejected.Add((m, reason!));
                continue;
            }
            if (m.IsStatic)
            {
                staticFallback ??= m;
            }
            else
            {
                candidate ??= m;
            }
        }

        var chosen = candidate ?? staticFallback;
        if (chosen is null)
        {
            // No usable shape was found. If the user wrote a method literally named
            // Configure on a [Command] type, they almost certainly intended it as the
            // hook — surface why we couldn't wire it up so they can fix the signature
            // or rename the method instead of silently dropping it.
            foreach (var (method, reason) in rejected)
            {
                diagnostics.Add($"TXCL006:Method '{typeSymbol.Name}.{FormatConfigureSignature(method)}' is named 'Configure' on a [Command] type but cannot be used as a per-command Configure hook: {reason}. Rename the method or fix the signature (return void, and take any combination of IToolBuilder / IHostBuilder / IServiceCollection — no duplicates).");
            }
            return null;
        }

        var kinds = new ConfigureParamKind[chosen.Parameters.Length];
        for (var i = 0; i < chosen.Parameters.Length; i++)
        {
            kinds[i] = ClassifyConfigureParam(chosen.Parameters[i].Type);
        }
        return new ConfigureMethodModel(
            typeSymbol.ToDisplayString(FqnFormat),
            kinds.ToImmutableArray(),
            chosen.IsStatic);
    }

    private static string FormatConfigureSignature(IMethodSymbol method)
    {
        if (method.Parameters.Length == 0)
        {
            return "Configure()";
        }
        return "Configure(" + string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString())) + ")";
    }

    private static ConfigureParamKind ClassifyConfigureParam(ITypeSymbol type)
    {
        return type.ToDisplayString() switch
        {
            "triaxis.CommandLine.IToolBuilder" => ConfigureParamKind.ToolBuilder,
            "Microsoft.Extensions.Hosting.IHostBuilder" => ConfigureParamKind.HostBuilder,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection" => ConfigureParamKind.ServiceCollection,
            _ => ConfigureParamKind.None,
        };
    }

    private static ImmutableArray<ConstructorParameterModel> ExtractConstructorParameters(INamedTypeSymbol typeSymbol)
    {
        var ctors = typeSymbol.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .ToArray();

        // Prefer [ActivatorUtilitiesConstructor]-annotated constructor
        var chosen = ctors.FirstOrDefault(c =>
            c.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute"));

        // Otherwise pick the constructor with the most parameters
        chosen ??= ctors
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        if (chosen is null || chosen.Parameters.Length == 0)
        {
            return ImmutableArray<ConstructorParameterModel>.Empty;
        }

        return chosen.Parameters
            .Select(p => new ConstructorParameterModel(p.Type.ToDisplayString(FqnFormat)))
            .ToImmutableArray();
    }

    private static (ExecuteMethodModel Method, bool IsStandalone) DetectEntryPoint(INamedTypeSymbol typeSymbol)
    {
        // Walk the type hierarchy from the command type up through its base classes
        // and pick the first supported entry point encountered — any supported method
        // on a more derived class wins over anything on a base class, regardless of
        // shape. Within a single type the preference order is MainAsync (async standalone)
        // → Main (sync standalone, both mark the command as standalone — no service
        // provider, no middleware) → ExecuteAsync(CancellationToken) → ExecuteAsync()
        // → Execute(). Recognised standalone shapes are:
        //   MainAsync()                                 — Task / Task<int>
        //   MainAsync(CancellationToken)                — Task / Task<int>
        //   MainAsync(IToolBuilder)                     — Task / Task<int>
        //   MainAsync(IToolBuilder, CancellationToken)  — Task / Task<int>
        //   Main()                                      — void / int
        //   Main(CancellationToken)                     — void / int
        //   Main(IToolBuilder)                          — void / int
        //   Main(IToolBuilder, CancellationToken)       — void / int
        // Private members on base types aren't callable from the generated sibling
        // action class, so we skip them.
        for (var current = typeSymbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            var isBase = !SymbolEqualityComparer.Default.Equals(current, typeSymbol);
            var mainAsyncCandidates = current.GetMembers("MainAsync")
                .OfType<IMethodSymbol>()
                .Where(m => !isBase || m.DeclaredAccessibility != Accessibility.Private)
                .ToArray();
            var mainCandidates = current.GetMembers("Main")
                .OfType<IMethodSymbol>()
                .Where(m => !isBase || m.DeclaredAccessibility != Accessibility.Private)
                .ToArray();
            var executeAsyncCandidates = current.GetMembers("ExecuteAsync")
                .OfType<IMethodSymbol>()
                .Where(m => !isBase || m.DeclaredAccessibility != Accessibility.Private)
                .ToArray();
            var executeCandidates = current.GetMembers("Execute")
                .OfType<IMethodSymbol>()
                .Where(m => !isBase || m.DeclaredAccessibility != Accessibility.Private)
                .ToArray();

            foreach (var m in mainAsyncCandidates)
            {
                var acceptsBuilder = false;
                var acceptsCt = false;
                var shapeOk = m.Parameters.Length switch
                {
                    0 => true,
                    1 => IsCtParam(m.Parameters[0], ref acceptsCt) || IsBuilderParam(m.Parameters[0], ref acceptsBuilder),
                    2 => IsBuilderParam(m.Parameters[0], ref acceptsBuilder) && IsCtParam(m.Parameters[1], ref acceptsCt),
                    _ => false,
                };
                if (!shapeOk)
                {
                    continue;
                }

                var (kind, innerType) = AnalyzeReturnType(m.ReturnType);
                // MainAsync must return Task or Task<int>; other return shapes fall through
                // (a sync `Main` should be used for void/int returns instead — `void
                // MainAsync()` would be a contradiction in terms).
                if (kind is not (ReturnKind.Task or ReturnKind.TaskOfInt))
                {
                    continue;
                }

                return (new ExecuteMethodModel("MainAsync", true, acceptsCt,
                    m.ReturnType.ToDisplayString(FqnFormat), kind, innerType,
                    AcceptsToolBuilder: acceptsBuilder), IsStandalone: true);
            }

            foreach (var m in mainCandidates)
            {
                var acceptsBuilder = false;
                var acceptsCt = false;
                var shapeOk = m.Parameters.Length switch
                {
                    0 => true,
                    1 => IsCtParam(m.Parameters[0], ref acceptsCt) || IsBuilderParam(m.Parameters[0], ref acceptsBuilder),
                    2 => IsBuilderParam(m.Parameters[0], ref acceptsBuilder) && IsCtParam(m.Parameters[1], ref acceptsCt),
                    _ => false,
                };
                if (!shapeOk)
                {
                    continue;
                }

                var (kind, innerType) = AnalyzeReturnType(m.ReturnType);
                // Sync Main: void / int only. Task-returning entry points should be
                // named MainAsync.
                if (kind is not (ReturnKind.Void or ReturnKind.Int))
                {
                    continue;
                }

                return (new ExecuteMethodModel("Main", IsAsync: false, acceptsCt,
                    m.ReturnType.ToDisplayString(FqnFormat), kind, innerType,
                    AcceptsToolBuilder: acceptsBuilder), IsStandalone: true);
            }

            foreach (var m in executeAsyncCandidates)
            {
                if (m.Parameters.Length == 1 &&
                    m.Parameters[0].Type.ToDisplayString() == "System.Threading.CancellationToken")
                {
                    var (kind, innerType) = AnalyzeReturnType(m.ReturnType);
                    return (new ExecuteMethodModel("ExecuteAsync", true, true,
                        m.ReturnType.ToDisplayString(FqnFormat),
                        kind, innerType), IsStandalone: false);
                }
            }

            foreach (var m in executeAsyncCandidates)
            {
                if (m.Parameters.Length == 0)
                {
                    var (kind, innerType) = AnalyzeReturnType(m.ReturnType);
                    return (new ExecuteMethodModel("ExecuteAsync", true, false,
                        m.ReturnType.ToDisplayString(FqnFormat),
                        kind, innerType), IsStandalone: false);
                }
            }

            foreach (var m in executeCandidates)
            {
                if (m.Parameters.Length == 0)
                {
                    var (kind, innerType) = AnalyzeReturnType(m.ReturnType);
                    return (new ExecuteMethodModel("Execute", false, false,
                        m.ReturnType.ToDisplayString(FqnFormat),
                        kind, innerType), IsStandalone: false);
                }
            }
        }

        return (new ExecuteMethodModel("ExecuteAsync", true, false, "global::System.Threading.Tasks.Task", ReturnKind.Task, null), IsStandalone: false);

        static bool IsCtParam(IParameterSymbol p, ref bool accepts)
        {
            if (p.Type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                accepts = true;
                return true;
            }
            return false;
        }

        static bool IsBuilderParam(IParameterSymbol p, ref bool accepts)
        {
            if (p.Type.ToDisplayString() == "triaxis.CommandLine.IToolBuilder")
            {
                accepts = true;
                return true;
            }
            return false;
        }
    }

    private static (ReturnKind Kind, string? InnerTypeFqn) AnalyzeReturnType(ITypeSymbol returnType)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return (ReturnKind.Void, null);
        }

        if (returnType.SpecialType == SpecialType.System_Int32)
        {
            return (ReturnKind.Int, null);
        }

        var display = returnType.ToDisplayString();

        if (display == "System.Threading.Tasks.Task")
        {
            return (ReturnKind.Task, null);
        }

        if (returnType is INamedTypeSymbol { IsGenericType: true } nts)
        {
            var origDef = nts.OriginalDefinition.ToDisplayString();

            // Task<T>
            if (origDef == "System.Threading.Tasks.Task<TResult>")
            {
                var inner = nts.TypeArguments[0];

                if (inner.SpecialType == SpecialType.System_Int32)
                {
                    return (ReturnKind.TaskOfInt, null);
                }

                if (IsICommandInvocationResult(inner))
                {
                    return (ReturnKind.TaskOfICommandInvocationResult, null);
                }

                var elementType = GetIEnumerableElementType(inner);
                if (elementType is not null)
                {
                    return (ReturnKind.TaskOfIEnumerableOfT, elementType.ToDisplayString(FqnFormat));
                }

                return (ReturnKind.TaskOfT, inner.ToDisplayString(FqnFormat));
            }

            // IAsyncEnumerable<T>
            if (origDef == "System.Collections.Generic.IAsyncEnumerable<T>")
            {
                var elementFqn = nts.TypeArguments[0].ToDisplayString(FqnFormat);
                return (ReturnKind.IAsyncEnumerableOfT, elementFqn);
            }
        }

        // ICommandInvocationResult (non-generic, direct)
        if (IsICommandInvocationResult(returnType))
        {
            return (ReturnKind.ICommandInvocationResult, null);
        }

        // IEnumerable<T>
        var syncElement = GetIEnumerableElementType(returnType);
        if (syncElement is not null)
        {
            return (ReturnKind.IEnumerableOfT, syncElement.ToDisplayString(FqnFormat));
        }

        // Plain value
        return (ReturnKind.Value, returnType.ToDisplayString(FqnFormat));
    }

    private static bool IsICommandInvocationResult(ITypeSymbol type)
    {
        var display = type.ToDisplayString();
        if (display is "triaxis.CommandLine.ICommandInvocationResult"
            or "triaxis.CommandLine.ICommandInvocationResult?")
        {
            return true;
        }

        return type.AllInterfaces.Any(i =>
            i.ToDisplayString() == "triaxis.CommandLine.ICommandInvocationResult");
    }

    private static ITypeSymbol? GetIEnumerableElementType(ITypeSymbol type)
    {
        // Check direct implementation
        if (type is INamedTypeSymbol { IsGenericType: true } directNts)
        {
            var directOrig = directNts.OriginalDefinition.ToDisplayString();
            if (directOrig == "System.Collections.Generic.IEnumerable<T>")
            {
                var elem = directNts.TypeArguments[0];
                if (elem.SpecialType is not (SpecialType.System_Byte or SpecialType.System_Char))
                {
                    return elem;
                }
                return null;
            }
        }

        // Check interfaces
        foreach (var iface in type.AllInterfaces)
        {
            if (iface is { IsGenericType: true } ifaceNts)
            {
                var ifaceOrig = ifaceNts.OriginalDefinition.ToDisplayString();
                if (ifaceOrig == "System.Collections.Generic.IEnumerable<T>")
                {
                    var elem = ifaceNts.TypeArguments[0];
                    if (elem.SpecialType is not (SpecialType.System_Byte or SpecialType.System_Char))
                    {
                        return elem;
                    }
                }
            }
        }

        return null;
    }

    private static void CollectMembers(INamedTypeSymbol typeSymbol, List<MemberModel> members,
        System.Threading.CancellationToken ct, AccessPathSegment[]? accessPath = null)
    {
        foreach (var member in typeSymbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (member is not (IFieldSymbol or IPropertySymbol))
            {
                continue;
            }

            foreach (var attr in member.GetAttributes())
            {
                var attrName = attr.AttributeClass?.ToDisplayString();
                var memberType = member switch
                {
                    IFieldSymbol f => f.Type,
                    IPropertySymbol p => p.Type,
                    _ => null
                };

                if (memberType is null)
                {
                    continue;
                }

                // Preserve the originally declared type (e.g. "int?") for accessor
                // signatures and reflection casts. The unwrapped form below (e.g. "int")
                // is what System.CommandLine uses for parsing — but the actual backing
                // field/property is still typed as the nullable form, so [UnsafeAccessor]
                // must match that signature exactly or it fails at runtime.
                var declaredTypeFqn = memberType.ToDisplayString(FqnFormat);

                var isNullable = false;
                if (memberType is INamedTypeSymbol nts &&
                    nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                    nts.TypeArguments.Length == 1)
                {
                    memberType = nts.TypeArguments[0];
                    isNullable = true;
                }
                else if (memberType.NullableAnnotation == NullableAnnotation.Annotated)
                {
                    isNullable = true;
                }

                var memberTypeFqn = memberType.ToDisplayString(FqnFormat);
                var isField = member is IFieldSymbol;
                var isPublic = member.DeclaredAccessibility == Accessibility.Public;
                var hasSetter = member is IFieldSymbol || (member is IPropertySymbol prop && prop.SetMethod is { IsInitOnly: false });
                var isInitOnly = member is IPropertySymbol { SetMethod.IsInitOnly: true };
                // Auto-properties (and fields) have a synthesized `<X>k__BackingField`; properties with
                // a custom getter/setter body do not, so accessor emission must fall back to calling
                // the property accessor methods instead of touching a backing field that doesn't exist.
                var hasBackingField = isField || (member is IPropertySymbol propBf && IsAutoProperty(propBf));
                var declaringTypeFqn = member.ContainingType.ToDisplayString(FqnFormat);
                var isMemberRequired = IsMemberRequired(member);
                var isCollection = GetIEnumerableElementType(memberType) is not null;

                switch (attrName)
                {
                    case "triaxis.CommandLine.ArgumentAttribute":
                    {
                        var name = GetNamedArgString(attr, "Name")
                            ?? GetConstructorArgString(attr, 0);
                        var desc = GetNamedArgString(attr, "Description")
                            ?? GetConstructorArgString(attr, 1);
                        var order = GetNamedArgDouble(attr, "Order");
                        var required = GetNamedArgBool(attr, "Required");
                        var requiredIsSet = attr.NamedArguments.Any(n => n.Key == "Required");
                        members.Add(new MemberModel(
                            MemberKind.Argument, member.Name, memberTypeFqn, declaredTypeFqn, isField, isPublic, hasSetter, isInitOnly, hasBackingField,
                            declaringTypeFqn, name, desc, null,
                            requiredIsSet ? required : null,
                            isMemberRequired, isCollection, isNullable,
                            order, null, accessPath ?? Array.Empty<AccessPathSegment>()));
                        break;
                    }
                    case "triaxis.CommandLine.OptionAttribute":
                    {
                        var name = GetNamedArgString(attr, "Name")
                            ?? GetConstructorArgString(attr, 0);
                        var desc = GetNamedArgString(attr, "Description");
                        var optAliases = GetConstructorArgStringArray(attr, 1)
                            ?? GetNamedArgStringArray(attr, "Aliases");
                        var order = GetNamedArgDouble(attr, "Order");
                        var required = GetNamedArgBool(attr, "Required");
                        var requiredIsSet = attr.NamedArguments.Any(n => n.Key == "Required");
                        members.Add(new MemberModel(
                            MemberKind.Option, member.Name, memberTypeFqn, declaredTypeFqn, isField, isPublic, hasSetter, isInitOnly, hasBackingField,
                            declaringTypeFqn, name, desc, optAliases,
                            requiredIsSet ? required : null,
                            isMemberRequired, isCollection, isNullable,
                            order, null, accessPath ?? Array.Empty<AccessPathSegment>()));
                        break;
                    }
                    case "triaxis.CommandLine.OptionsAttribute":
                    {
                        if (memberType is INamedTypeSymbol nestedType)
                        {
                            var segment = new AccessPathSegment(
                                member.Name, memberTypeFqn, declaredTypeFqn, isField, isPublic, hasSetter, hasBackingField, isMemberRequired, declaringTypeFqn);
                            var newPath = (accessPath ?? Array.Empty<AccessPathSegment>()).Append(segment).ToArray();
                            for (var current = nestedType; current is not null; current = current.BaseType)
                            {
                                if (current.ToDisplayString() is "object" or "System.Object")
                                {
                                    break;
                                }
                                CollectMembers(current, members, ct, newPath);
                            }
                        }
                        break;
                    }
                    case "triaxis.CommandLine.InjectAttribute":
                    {
                        var injectType = GetNamedArgType(attr, "Type");
                        members.Add(new MemberModel(
                            MemberKind.Inject, member.Name, memberTypeFqn, declaredTypeFqn, isField, isPublic, hasSetter, isInitOnly, hasBackingField,
                            declaringTypeFqn, null, null, null, null, isMemberRequired, false, false,
                            0, injectType, accessPath ?? Array.Empty<AccessPathSegment>()));
                        break;
                    }
                }
            }
        }
    }

    private static bool IsMemberRequired(ISymbol member)
    {
        return member switch
        {
            IFieldSymbol f => f.IsRequired,
            IPropertySymbol p => p.IsRequired,
            _ => false,
        };
    }

    /// <summary>
    /// True when the property is auto-implemented (and therefore has a compiler-synthesized
    /// <c>&lt;Name&gt;k__BackingField</c>). Properties whose accessors have explicit bodies
    /// — even just one — do not get a backing field, and the generator must call the
    /// property accessor methods instead of trying to touch the field directly.
    /// </summary>
    private static bool IsAutoProperty(IPropertySymbol property)
    {
        // Properties from metadata have no syntax to inspect; assume auto so we keep
        // the existing backing-field code path for compiled inputs.
        if (property.DeclaringSyntaxReferences.Length == 0)
        {
            return true;
        }

        foreach (var syntaxRef in property.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is PropertyDeclarationSyntax pds)
            {
                // Expression-bodied property (`public int X => 1;`) — no backing field.
                if (pds.ExpressionBody is not null)
                {
                    return false;
                }
                if (pds.AccessorList is null)
                {
                    return false;
                }
                foreach (var accessor in pds.AccessorList.Accessors)
                {
                    if (accessor.Body is not null || accessor.ExpressionBody is not null)
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private static string? GetNamedArgString(AttributeData attr, string name)
    {
        var arg = attr.NamedArguments.FirstOrDefault(n => n.Key == name);
        return arg.Value.Value?.ToString();
    }

    private static bool GetNamedArgBool(AttributeData attr, string name)
    {
        var arg = attr.NamedArguments.FirstOrDefault(n => n.Key == name);
        return arg.Value.Value is true;
    }

    private static double GetNamedArgDouble(AttributeData attr, string name)
    {
        var arg = attr.NamedArguments.FirstOrDefault(n => n.Key == name);
        if (arg.Value.Value is double d)
        {
            return d;
        }
        if (arg.Value.Value is int i)
        {
            return i;
        }
        return 0;
    }

    private static string? GetNamedArgType(AttributeData attr, string name)
    {
        var arg = attr.NamedArguments.FirstOrDefault(n => n.Key == name);
        if (arg.Value.Value is INamedTypeSymbol nts)
        {
            return nts.ToDisplayString(FqnFormat);
        }
        return null;
    }

    private static string[]? GetNamedArgStringArray(AttributeData attr, string name)
    {
        var arg = attr.NamedArguments.FirstOrDefault(n => n.Key == name);
        if (arg.Value.Kind == TypedConstantKind.Array)
        {
            return arg.Value.Values.Select(v => v.Value?.ToString()!).ToArray();
        }
        return null;
    }

    private static string? GetConstructorArgString(AttributeData attr, int index)
    {
        if (index < attr.ConstructorArguments.Length)
        {
            var arg = attr.ConstructorArguments[index];
            return arg.Value?.ToString();
        }
        return null;
    }

    private static string[]? GetConstructorArgStringArray(AttributeData attr, int index)
    {
        if (index < attr.ConstructorArguments.Length)
        {
            var arg = attr.ConstructorArguments[index];
            if (arg.Kind == TypedConstantKind.Array)
            {
                return arg.Values.Select(v => v.Value?.ToString()!).ToArray();
            }
        }
        return null;
    }

    private static string GenerateSource(ImmutableArray<CommandModel> commands, ImmutableArray<AssemblyCommandModel> assemblyCommands, string assemblyName, bool hasUnsafeAccessor, bool hasOperatingSystemIsOSPlatform)
    {
        var sw = new StringWriter();
        var w = new IndentedTextWriter(sw);

        w.WriteLine("// <auto-generated/>");
        w.WriteLine("#nullable enable");
        w.WriteLine("#pragma warning disable CS8601 // Possible null reference assignment (UnsafeAccessor ref returns)");
        w.WriteLine();
        w.WriteLine("using System;");
        w.WriteLine("using System.CommandLine;");
        w.WriteLine("using System.CommandLine.Invocation;");
        w.WriteLine("using System.CommandLine.Parsing;");
        w.WriteLine("using System.Reflection;");
        w.WriteLine("using System.Runtime.CompilerServices;");
        w.WriteLine("using System.Threading;");
        w.WriteLine("using System.Threading.Tasks;");
        w.WriteLine("using Microsoft.Extensions.DependencyInjection;");
        w.WriteLine("using triaxis.CommandLine;");
        w.WriteLine("using triaxis.CommandLine.Invocation;");
        w.WriteLine();
        w.Block("namespace triaxis.CommandLine.Generated", () =>
        {
            w.Block("internal static class GeneratedCommandRegistration_", () =>
            {
                // Module initializer
                w.WriteLine("[ModuleInitializer]");
                w.Block("internal static void Register()", () =>
                {
                    w.WriteLine($"GeneratedCommandRegistration.Register({FormatString(assemblyName)}, CreateCommandTree);");
                });
                w.WriteLine();

                // Tree factory — builds the command tree model and returns the root
                w.Block("private static CommandTreeNode CreateCommandTree(Func<IServiceProvider> getServiceProvider)", () =>
                {
                    GenerateTreeConstruction(w, commands, assemblyCommands, hasOperatingSystemIsOSPlatform);
                });
                w.WriteLine();
            });
            w.WriteLine();

            // Per-command action classes (skip commands whose validation already failed)
            foreach (var cmd in commands)
            {
                if (!cmd.Diagnostics.IsDefaultOrEmpty)
                {
                    continue;
                }
                GenerateCommandAction(w, cmd, hasUnsafeAccessor);
            }
        });

        w.Flush();
        return sw.ToString();
    }

    /// <summary>
    /// Emits the body of CreateCommandTree — builds a RootCommand with nested Subcommands
    /// using object/collection initializer syntax, fully sorted at generation time.
    /// </summary>
    private static void GenerateTreeConstruction(IndentedTextWriter w,
        ImmutableArray<CommandModel> commands,
        ImmutableArray<AssemblyCommandModel> assemblyCommands,
        bool hasOperatingSystemIsOSPlatform)
    {
        // Build a generation-time tree from all command paths and assembly commands
        var root = new GenTreeNode("");

        foreach (var cmd in commands)
        {
            var node = root;
            foreach (var segment in cmd.Path)
            {
                node = node.GetOrCreateChild(segment);
            }
            node.Command = cmd;
        }

        foreach (var asmCmd in assemblyCommands)
        {
            var node = root;
            foreach (var segment in asmCmd.Path)
            {
                node = node.GetOrCreateChild(segment);
            }
            node.AssemblyCommand = asmCmd;
        }

        // Emit the tree as a CommandTreeNode with nested initializers
        w.Write("return new CommandTreeNode(\"\")");
        EmitNodeInitializer(w, root, hasOperatingSystemIsOSPlatform, suffix: ";");
    }

    /// <summary>
    /// Emits the object initializer block for a tree node.
    /// </summary>
    private static void EmitNodeInitializer(IndentedTextWriter w, GenTreeNode node, bool hasOperatingSystemIsOSPlatform, string suffix = "")
    {
        var children = node.Children.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Value).ToArray();

        var desc = node.AssemblyCommand?.Description ?? node.Command?.Description;
        var aliases = node.AssemblyCommand?.Aliases ?? node.Command?.Aliases;
        var supportedPlatforms = node.Command?.SupportedPlatforms;
        var hasAction = node.Command is not null;
        var hasContent = desc is not null || aliases is { Length: > 0 }
            || supportedPlatforms is { Length: > 0 } || hasAction || children.Length > 0;

        if (!hasContent)
        {
            w.WriteLine(suffix);
            return;
        }

        w.WriteLine();
        w.Block(suffix: suffix, body: () =>
        {
            if (desc is not null)
            {
                w.WriteLine($"Description = {FormatString(desc)},");
            }

            if (aliases is { Length: > 0 })
            {
                w.WriteLine($"Aliases = {FormatStringArrayInline(aliases)},");
            }

            if (supportedPlatforms is { Length: > 0 })
            {
                var (expr, needsCA1418Suppression) = FormatPlatformCheck(supportedPlatforms, hasOperatingSystemIsOSPlatform);
                if (needsCA1418Suppression)
                {
                    // Scope the suppression to exactly the one line that calls the
                    // string-based IsOSPlatform with an unrecognized platform name.
                    w.WriteLine("#pragma warning disable CA1418 // Unknown platform name passed through from user-authored [SupportedOSPlatform]");
                }
                w.WriteLine($"IsSupported = {expr},");
                if (needsCA1418Suppression)
                {
                    w.WriteLine("#pragma warning restore CA1418");
                }
            }

            if (node.Command is { } cmd && cmd.Diagnostics.IsDefaultOrEmpty)
            {
                var safeName = GetSafeName(cmd);
                var args = GetArguments(cmd);
                var opts = GetOptions(cmd);
                var actionOptions = cmd.ActionOptions.IsDefault
                    ? ImmutableArray<ActionOptionModel>.Empty
                    : cmd.ActionOptions;

                w.WriteLine(cmd.IsStandalone
                    ? $"Action = new {safeName}.Action(),"
                    : $"Action = new {safeName}.Action(getServiceProvider),");
                if (args.Length > 0)
                {
                    w.Block("Arguments =", suffix: ",", body: () =>
                    {
                        foreach (var arg in args)
                        {
                            w.Write($"new ArgumentDefinition<{arg.MemberTypeFqn}>({FormatString(GetCliName(arg))})");
                            EmitArgOptInitializer(w, arg);
                            w.WriteLine(",");
                        }
                    });
                }
                if (opts.Length > 0 || actionOptions.Length > 0)
                {
                    w.Block("Options =", suffix: ",", body: () =>
                    {
                        foreach (var opt in opts)
                        {
                            var nameAndAliases = new List<string> { GetCliName(opt) };
                            if (opt.Aliases is not null)
                            {
                                nameAndAliases.AddRange(opt.Aliases);
                            }
                            var aliasesArr = nameAndAliases.Skip(1).ToArray();
                            var nameArg = aliasesArr.Length > 0
                                ? $"{FormatString(nameAndAliases[0])}, {FormatStringArrayInline(aliasesArr)}"
                                : FormatString(nameAndAliases[0]);
                            w.Write($"new OptionDefinition<{opt.MemberTypeFqn}>({nameArg})");
                            EmitArgOptInitializer(w, opt);
                            w.WriteLine(",");
                        }
                        foreach (var ao in actionOptions)
                        {
                            var nameAndAliases = new List<string> { GetActionOptionCliName(ao) };
                            if (ao.Aliases is not null)
                            {
                                nameAndAliases.AddRange(ao.Aliases);
                            }
                            var aliasesArr = nameAndAliases.Skip(1).ToArray();
                            var nameArg = aliasesArr.Length > 0
                                ? $"{FormatString(nameAndAliases[0])}, {FormatStringArrayInline(aliasesArr)}"
                                : FormatString(nameAndAliases[0]);
                            // Each action option has its own nested class named after the
                            // user's method, so the wiring reads as `{safeName}.{methodName}`.
                            // Standalone-style classes (those whose method takes IToolBuilder
                            // — or that sit on a standalone primary) take no ctor args; DI
                            // classes take the service provider accessor.
                            var actionClassName = $"{safeName}.{ao.MethodName}";
                            var actionIsStandalone = ao.AcceptsToolBuilder || cmd.IsStandalone;
                            var actionCtorArgs = actionIsStandalone ? "" : "getServiceProvider";
                            w.Write($"new OptionDefinition<bool>({nameArg})");
                            var initParts = new List<string>();
                            if (ao.Description is not null)
                            {
                                initParts.Add($"Description = {FormatString(ao.Description)}");
                            }
                            initParts.Add("Arity = ArgumentArity.ZeroOrOne");
                            initParts.Add($"Action = new {actionClassName}({actionCtorArgs})");
                            w.Write(" { " + string.Join(", ", initParts) + " }");
                            w.WriteLine(",");
                        }
                    });
                }
            }

            if (children.Length > 0)
            {
                w.Block("Subcommands =", suffix: ",", body: () =>
                {
                    foreach (var child in children)
                    {
                        w.Write($"new CommandTreeNode({FormatString(child.Name)})");
                        EmitNodeInitializer(w, child, hasOperatingSystemIsOSPlatform, suffix: ",");
                    }
                });
            }
        });
    }

    /// <summary>
    /// Emits a boolean expression that is <see langword="true"/> when the current OS matches
    /// any of the <paramref name="platforms"/>. On net5+ this uses the dedicated typed
    /// methods on <c>System.OperatingSystem</c> (e.g. <c>IsWindows()</c>). On older TFMs
    /// the generator falls back to <c>RuntimeInformation.IsOSPlatform(...)</c>, preferring
    /// the predefined <c>OSPlatform</c> fields for known platforms.
    /// Returns the generated expression and a flag indicating whether the net5+ path
    /// fell back to <c>OperatingSystem.IsOSPlatform(string)</c> for any entry — the caller
    /// wraps the line in <c>#pragma warning disable CA1418</c> when that's the case.
    /// </summary>
    private static (string Expression, bool NeedsCA1418Suppression) FormatPlatformCheck(
        string[] platforms, bool hasOperatingSystemIsOSPlatform)
    {
        if (!hasOperatingSystemIsOSPlatform)
        {
            return (string.Join(" || ", platforms.Select(FormatRuntimeInformationPlatformCheck)), false);
        }

        var parts = new string[platforms.Length];
        var hasFallback = false;
        for (var i = 0; i < platforms.Length; i++)
        {
            parts[i] = FormatOperatingSystemPlatformCheck(platforms[i], out var fellBack);
            hasFallback |= fellBack;
        }
        return (string.Join(" || ", parts), hasFallback);
    }

    /// <summary>
    /// Maps <c>[SupportedOSPlatform]</c> names to <c>System.OperatingSystem.Is&lt;X&gt;()</c>
    /// method suffixes, preserving the BCL's exact casing (<c>IOS</c>, <c>TvOS</c>, etc.).
    /// </summary>
    private static readonly Dictionary<string, string> s_osMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = "Windows",
            ["macos"] = "MacOS",
            ["linux"] = "Linux",
            ["freebsd"] = "FreeBSD",
            ["android"] = "Android",
            ["ios"] = "IOS",
            ["tvos"] = "TvOS",
            ["watchos"] = "WatchOS",
            ["maccatalyst"] = "MacCatalyst",
            ["browser"] = "Browser",
            ["wasi"] = "Wasi",
        };

    /// <summary>
    /// Predefined fields on <c>System.Runtime.InteropServices.OSPlatform</c>, used by the
    /// netstandard/netfx fallback.
    /// </summary>
    private static readonly Dictionary<string, string> s_osPlatformFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = "Windows",
            ["linux"] = "Linux",
            ["macos"] = "OSX",
            ["osx"] = "OSX",
            ["freebsd"] = "FreeBSD",
        };

    /// <summary>
    /// Extracts the platform name from a <c>[SupportedOSPlatform]</c> value, dropping any
    /// version suffix (e.g. <c>windows10.0</c> -> <c>windows</c>). Only the base platform
    /// name participates in command gating.
    /// </summary>
    private static string GetPlatformName(string platform)
        => new string(platform.TakeWhile(c => !char.IsDigit(c)).ToArray());

    private static string FormatOperatingSystemPlatformCheck(string platform, out bool fellBackToStringApi)
    {
        var name = GetPlatformName(platform);
        if (s_osMethods.TryGetValue(name, out var suffix))
        {
            fellBackToStringApi = false;
            return $"global::System.OperatingSystem.Is{suffix}()";
        }
        // Unknown platform name — no dedicated method exists. Pass the value through so
        // the generator stays forward-compatible with names the BCL may add.
        fellBackToStringApi = true;
        return $"global::System.OperatingSystem.IsOSPlatform({FormatString(platform)})";
    }

    private static string FormatRuntimeInformationPlatformCheck(string platform)
    {
        var name = GetPlatformName(platform);
        var osPlatformExpr = s_osPlatformFields.TryGetValue(name, out var field)
            ? $"global::System.Runtime.InteropServices.OSPlatform.{field}"
            : $"global::System.Runtime.InteropServices.OSPlatform.Create({FormatString(name.ToUpperInvariant())})";
        return $"global::System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform({osPlatformExpr})";
    }

    private static void EmitArgOptInitializer(IndentedTextWriter w, MemberModel member)
    {
        var initParts = new List<string>();
        if (member.Description is not null)
        {
            initParts.Add($"Description = {FormatString(member.Description)}");
        }
        if (member.Kind == MemberKind.Option && (member.Required == true || member.IsMemberRequired))
        {
            initParts.Add("Required = true");
        }
        var isBool = member.MemberTypeFqn is "bool" or "global::System.Boolean";
        var isOptional = member.Required == false || member.IsNullable;
        var isRequired = member.Required == true || member.IsMemberRequired;
        string arity;
        if (isBool)
        {
            arity = "ZeroOrOne";
        }
        else if (member.IsCollection)
        {
            arity = member.Kind == MemberKind.Argument && !isRequired ? "ZeroOrMore" : "OneOrMore";
        }
        else if (isOptional)
        {
            arity = "ZeroOrOne";
        }
        else
        {
            arity = "ExactlyOne";
        }
        initParts.Add($"Arity = ArgumentArity.{arity}");
        if (initParts.Count > 0)
        {
            w.Write(" { " + string.Join(", ", initParts) + " }");
        }
    }

    private class GenTreeNode(string name)
    {
        public string Name { get; } = name;
        public SortedDictionary<string, GenTreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CommandModel? Command { get; set; }
        public AssemblyCommandModel? AssemblyCommand { get; set; }

        public GenTreeNode GetOrCreateChild(string childName)
        {
            if (!Children.TryGetValue(childName, out var child))
            {
                Children[childName] = child = new GenTreeNode(childName);
            }
            return child;
        }
    }

    private static void GenerateCommandAction(IndentedTextWriter w, CommandModel cmd, bool hasUnsafeAccessor)
    {
        var actionOptions = cmd.ActionOptions.IsDefault
            ? ImmutableArray<ActionOptionModel>.Empty
            : cmd.ActionOptions;

        // Each entry point (the primary plus every [ActionOption]) lives in its own
        // class so its kind (DI vs standalone) can be chosen independently from the
        // command's primary kind. The whole per-command emission — accessors, the
        // CreateInstance / BindOptions / InjectServices lifecycle, and every entry-
        // point class — is wrapped in one `internal static class {safeName}` umbrella,
        // so call sites read as `greet.Action` or `greet.MigrateAsync` and the binder
        // helpers resolve as siblings without a class-name prefix from inside.
        var anyDi = !cmd.IsStandalone || actionOptions.Any(a => !a.AcceptsToolBuilder);
        var anyStandalone = cmd.IsStandalone || actionOptions.Any(a => a.AcceptsToolBuilder);
        var safeName = GetSafeName(cmd);

        w.Block($"internal static class {safeName}", () =>
        {
            EmitBindHelperBody(w, cmd, hasUnsafeAccessor, emitDi: anyDi, emitStandalone: anyStandalone);

            // Primary entry point — always named `Action` so the outer tree wiring
            // (`new {safeName}.Action(...)`) is uniform across commands.
            if (cmd.IsStandalone)
            {
                EmitStandaloneActionClass(w, cmd, "Action",
                    cmd.ExecuteMethod.MethodName, cmd.ExecuteMethod.AcceptsToolBuilder,
                    cmd.ExecuteMethod.AcceptsCancellationToken, cmd.ExecuteMethod.ReturnKind,
                    emitDi: anyDi, emitStandalone: anyStandalone);
            }
            else
            {
                EmitDiActionClass(w, cmd, "Action",
                    cmd.ExecuteMethod.MethodName, cmd.ExecuteMethod.AcceptsCancellationToken,
                    cmd.ExecuteMethod.ReturnKind, cmd.ExecuteMethod.InnerTypeFqn,
                    emitDi: anyDi, emitStandalone: anyStandalone);
            }

            foreach (var ao in actionOptions)
            {
                // Action option class — named after the user's method so call sites
                // read as `{safeName}.{methodName}`.
                var className = ao.MethodName;
                if (ao.AcceptsToolBuilder)
                {
                    EmitStandaloneActionClass(w, cmd, className, ao.MethodName,
                        acceptsToolBuilder: true, ao.AcceptsCancellationToken, ao.ReturnKind,
                        emitDi: anyDi, emitStandalone: anyStandalone);
                }
                else if (cmd.IsStandalone)
                {
                    // Action option without IToolBuilder on a standalone primary still runs
                    // through the standalone path (no DI), since the command's own state is
                    // standalone-only (no [Inject]/ctor-DI usable from this action).
                    EmitStandaloneActionClass(w, cmd, className, ao.MethodName,
                        acceptsToolBuilder: false, ao.AcceptsCancellationToken, ao.ReturnKind,
                        emitDi: anyDi, emitStandalone: anyStandalone);
                }
                else
                {
                    EmitDiActionClass(w, cmd, className, ao.MethodName,
                        ao.AcceptsCancellationToken, ao.ReturnKind, ao.InnerTypeFqn,
                        emitDi: anyDi, emitStandalone: anyStandalone);
                }
            }
        });
        w.WriteLine();
    }

    /// <summary>
    /// Emits the per-command binder body — accessors, the <c>CreateInstance</c> /
    /// <c>BindOptions</c> / <c>InjectServices</c> trio — directly into the surrounding
    /// umbrella class. The umbrella wrapper is opened by <see cref="GenerateCommandAction"/>
    /// since it also nests the action classes; sharing the same enclosing scope means
    /// the action classes can call the binder helpers as siblings (no class-name prefix).
    /// </summary>
    private static void EmitBindHelperBody(IndentedTextWriter w, CommandModel cmd,
        bool hasUnsafeAccessor, bool emitDi, bool emitStandalone)
    {
        var args = GetArguments(cmd);
        var opts = GetOptions(cmd);
        var injects = cmd.Members.Where(m => m.Kind == MemberKind.Inject).ToArray();

        // Accessors for direct members that need backing field access.
        //   - Required [Argument]/[Option] members are set in the object initializer
        //     and don't need accessors.
        //   - All [Inject] members (required or not) need accessors so InjectServices
        //     can write them; for required injects the initializer assigns default!
        //     to satisfy the C# `required` modifier and InjectServices later replaces
        //     it with the resolved service.
        var memberAccessors = new Dictionary<string, Accessor>();
        foreach (var member in args.Concat(opts).Where(m => m.AccessPath.Length == 0 && !m.NeedsInitializer))
        {
            memberAccessors[MemberKey(member)] = EmitAccessor(
                w, member.DeclaringTypeFqn, member.MemberName, member.DeclaredTypeFqn,
                member.IsField, member.IsPublic, member.HasSetter, member.HasBackingField,
                $"__access_{GetMemberFieldName(member)}", hasUnsafeAccessor);
        }
        foreach (var member in injects.Where(m => m.AccessPath.Length == 0))
        {
            memberAccessors[MemberKey(member)] = EmitAccessor(
                w, member.DeclaringTypeFqn, member.MemberName, member.DeclaredTypeFqn,
                member.IsField, member.IsPublic, member.HasSetter, member.HasBackingField,
                $"__access_{GetMemberFieldName(member)}", hasUnsafeAccessor);
        }

        // Accessors for [Options] path segments
        var pathAccessors = new Dictionary<string, Accessor>();
        foreach (var member in args.Concat(opts).Concat(injects))
        {
            foreach (var seg in member.AccessPath)
            {
                var key = seg.OwnerTypeFqn + "." + seg.MemberName;
                if (pathAccessors.ContainsKey(key))
                {
                    continue;
                }
                pathAccessors[key] = EmitAccessor(
                    w, seg.OwnerTypeFqn, seg.MemberName, seg.DeclaredTypeFqn,
                    seg.IsField, seg.IsPublic, seg.HasSetter, seg.HasBackingField,
                    $"__access_path_{seg.MemberName}", hasUnsafeAccessor);
            }
        }

        // Accessors for members on nested [Options] types that need backing field access
        foreach (var member in args.Concat(opts).Where(m => m.AccessPath.Length > 0))
        {
            var lastSeg = member.AccessPath[member.AccessPath.Length - 1];
            memberAccessors[MemberKey(member)] = EmitAccessor(
                w, lastSeg.MemberTypeFqn, member.MemberName, member.DeclaredTypeFqn,
                member.IsField, member.IsPublic, member.HasSetter, member.HasBackingField,
                $"__access_{GetMemberFieldName(member)}", hasUnsafeAccessor);
        }

        // The lifecycle is split across three methods so the action class can stage
        // them as needed:
        //   CreateInstance  — constructs the command and writes any object-initializer
        //                     members (required [Argument]/[Option], required [Options]
        //                     segments, and `default!` placeholders for required
        //                     [Inject] so the C# `required` modifier is satisfied).
        //   BindOptions     — resolves nested [Options] containers and binds the
        //                     non-required [Argument]/[Option] members from ParseResult.
        //   InjectServices  — assigns every [Inject] member (required and non-required).
        // For instance-Configure commands, the action class calls CreateInstance +
        // BindOptions before invoking the user's Configure (so it can observe bound
        // values), then calls InjectServices later when the host is up.
        EmitCreateInstanceMethod(w, cmd, args, opts, injects,
            emitDi: emitDi, emitStandalone: emitStandalone);
        EmitBindOptionsMethod(w, cmd, args, opts, memberAccessors, pathAccessors);
        EmitInjectServicesMethod(w, cmd, injects, memberAccessors,
            emitDi: emitDi, emitStandalone: emitStandalone);
    }

    /// <summary>
    /// Emits the construction phase of the binder: <c>new T(ctor-DI)</c> with an object
    /// initializer that satisfies <c>required</c> members from <see cref="ParseResult"/>
    /// (args/opts) and required <c>[Options]</c> segments. Required <c>[Inject]</c>
    /// members are written with <c>default!</c> here purely to satisfy the C#
    /// <c>required</c> modifier; <see cref="EmitInjectServicesMethod"/> overwrites them
    /// with the resolved service. The provider parameter is only included when ctor DI
    /// is in play; standalone commands and instance-Configure commands (which forbid
    /// ctor DI) omit it entirely.
    /// </summary>
    private static void EmitCreateInstanceMethod(IndentedTextWriter w, CommandModel cmd,
        MemberModel[] args, MemberModel[] opts, MemberModel[] injects,
        bool emitDi, bool emitStandalone)
    {
        var requiredDirectArgs = args.Where(m => m.NeedsInitializer && m.AccessPath.Length == 0).ToArray();
        var requiredDirectOpts = opts.Where(m => m.NeedsInitializer && m.AccessPath.Length == 0).ToArray();
        var requiredInjects = injects.Where(m => m.NeedsInitializer).ToArray();
        var requiredOptionsProps = args.Concat(opts)
            .Where(m => m.AccessPath.Length > 0 && m.AccessPath[0].IsMemberRequired)
            .Select(m => m.AccessPath[0])
            .Distinct()
            .ToArray();
        var allMembers = args.Concat(opts).ToArray();
        var hasCtorParams = !cmd.ConstructorParameters.IsEmpty;
        var hasInitializer = requiredDirectArgs.Length > 0 || requiredDirectOpts.Length > 0
            || requiredInjects.Length > 0 || requiredOptionsProps.Length > 0;

        var standaloneOnly = emitStandalone && !emitDi;
        var hasOptionalProvider = emitDi && emitStandalone;
        // Provider is only referenced for ctor DI now — required [Inject] members are
        // written as `default!` placeholders here and resolved later in InjectServices.
        var needsProviderForConstruction = hasCtorParams && !standaloneOnly;
        var providerAccess = !needsProviderForConstruction
            ? null
            : hasOptionalProvider ? "provider?" : "provider";

        var sig = !needsProviderForConstruction
            ? $"internal static {cmd.TypeName} CreateInstance(ParseResult parseResult)"
            : hasOptionalProvider
                ? $"internal static {cmd.TypeName} CreateInstance(ParseResult parseResult, IServiceProvider? provider = null)"
                : $"internal static {cmd.TypeName} CreateInstance(ParseResult parseResult, IServiceProvider provider)";

        w.Block(sig, () =>
        {
            var ctorExpr = !needsProviderForConstruction
                ? $"new {cmd.TypeName}()"
                : $"new {cmd.TypeName}({string.Join(", ", cmd.ConstructorParameters.Select(p => $"{providerAccess}.GetRequiredService<{p.TypeFqn}>()!"))})";

            if (hasInitializer)
            {
                var initExpr = needsProviderForConstruction ? ctorExpr : $"new {cmd.TypeName}";
                w.Block($"var instance = {initExpr}", () =>
                {
                    foreach (var member in requiredDirectArgs.Cast<MemberModel>().Concat(requiredDirectOpts))
                    {
                        w.WriteLine($"{member.MemberName} = parseResult.GetValue<{member.MemberTypeFqn}>({FormatString(GetCliName(member))}),");
                    }
                    foreach (var inject in requiredInjects)
                    {
                        // Placeholder only — InjectServices replaces this with the resolved
                        // service. Without this line the C# `required` modifier rejects the
                        // initializer at compile time.
                        w.WriteLine($"{inject.MemberName} = default!,");
                    }
                    foreach (var seg in requiredOptionsProps)
                    {
                        var prefix = new[] { seg };
                        w.WriteLine($"{seg.MemberName} = {FormatOptionsCreateExpr(seg, prefix, 1, allMembers)},");
                    }
                }, suffix: ";");
            }
            else
            {
                w.WriteLine($"var instance = {ctorExpr};");
            }
            w.WriteLine("return instance;");
        });
        w.WriteLine();
    }

    /// <summary>
    /// Emits the binding phase: resolves any nested <c>[Options]</c> containers
    /// (creating intermediate instances when the user hasn't pre-populated them) and
    /// then walks <c>parseResult.CommandResult.Children</c> assigning bound values to
    /// the matching non-required <c>[Argument]</c> / <c>[Option]</c> members. Always
    /// emitted, even when the body is empty, so the action-class call sites can stay
    /// uniform.
    /// </summary>
    private static void EmitBindOptionsMethod(IndentedTextWriter w, CommandModel cmd,
        MemberModel[] args, MemberModel[] opts,
        Dictionary<string, Accessor> memberAccessors, Dictionary<string, Accessor> pathAccessors)
    {
        w.Block($"internal static void BindOptions({cmd.TypeName} instance, ParseResult parseResult)", () =>
        {
            GenerateOptionsPathResolution(w, args, opts, pathAccessors, memberAccessors);

            var bindableArgs = args.Where(a => !a.NeedsInitializer).ToArray();
            var bindableOpts = opts.Where(o => !o.NeedsInitializer).ToArray();

            if (bindableArgs.Length > 0 || bindableOpts.Length > 0)
            {
                w.Block("foreach (var __result in parseResult.CommandResult.Children)", () =>
                {
                    if (bindableArgs.Length > 0)
                    {
                        w.Block("if (__result is ArgumentResult { Tokens.Count: > 0 } __ar) switch (__ar.Argument.Name)", () =>
                        {
                            foreach (var arg in bindableArgs)
                            {
                                var assign = FormatMemberAssignment(arg, $"__ar.GetValueOrDefault<{arg.MemberTypeFqn}>()", memberAccessors[MemberKey(arg)]);
                                w.WriteLine($"case {FormatString(GetCliName(arg))}: {assign} break;");
                            }
                        });
                    }
                    if (bindableOpts.Length > 0)
                    {
                        w.Block("if (__result is OptionResult { Implicit: false } __or) switch (__or.Option.Name)", () =>
                        {
                            foreach (var opt in bindableOpts)
                            {
                                var assign = FormatMemberAssignment(opt, $"__or.GetValueOrDefault<{opt.MemberTypeFqn}>()", memberAccessors[MemberKey(opt)]);
                                w.WriteLine($"case {FormatString(GetCliName(opt))}: {assign} break;");
                            }
                        });
                    }
                });
            }
        });
        w.WriteLine();
    }

    /// <summary>
    /// Emits the post-construction inject phase: assigns every <c>[Inject]</c> member
    /// (required and non-required alike) from the service provider, overwriting the
    /// <c>default!</c> placeholders that <see cref="EmitCreateInstanceMethod"/> wrote
    /// for the required ones. Only emitted on the DI / mixed paths — the
    /// standalone-only path has no provider and validation forbids <c>[Inject]</c> on
    /// standalone commands. Returns true when the method was emitted, so callers can
    /// decide whether to call it.
    /// </summary>
    private static bool EmitInjectServicesMethod(IndentedTextWriter w, CommandModel cmd,
        MemberModel[] injects, Dictionary<string, Accessor> memberAccessors,
        bool emitDi, bool emitStandalone)
    {
        var standaloneOnly = emitStandalone && !emitDi;
        if (standaloneOnly || injects.Length == 0)
        {
            return false;
        }
        var hasOptionalProvider = emitDi && emitStandalone;
        var providerAccess = hasOptionalProvider ? "provider?" : "provider";
        var sig = hasOptionalProvider
            ? $"internal static void InjectServices({cmd.TypeName} instance, IServiceProvider? provider)"
            : $"internal static void InjectServices({cmd.TypeName} instance, IServiceProvider provider)";
        w.Block(sig, () =>
        {
            // Mixed mode: standalone callers pass null and the assignments short-circuit
            // to null on the corresponding members. Such commands' standalone entry
            // points are responsible for not dereferencing those fields.
            foreach (var inject in injects)
            {
                var serviceType = inject.InjectTypeFqn ?? inject.MemberTypeFqn;
                w.WriteLine(FormatWrite(memberAccessors[MemberKey(inject)], "instance",
                    $"{providerAccess}.GetRequiredService<{serviceType}>()!") + ";");
            }
        });
        w.WriteLine();
        return true;
    }

    /// <summary>
    /// Mirrors the emission predicate of <see cref="EmitInjectServicesMethod"/> so action
    /// classes can decide whether to call <c>InjectServices</c> without re-running the
    /// emission logic. Keep these two in sync.
    /// </summary>
    private static bool BinderEmitsInjectServices(CommandModel cmd, bool emitDi, bool emitStandalone)
    {
        if (emitStandalone && !emitDi)
        {
            return false;
        }
        return cmd.Members.Any(m => m.Kind == MemberKind.Inject);
    }

    /// <summary>
    /// Mirrors the signature decision in <see cref="EmitCreateInstanceMethod"/>: the
    /// binder's <c>CreateInstance</c> only takes a provider when ctor DI is in play
    /// (and never on the standalone-only path). <c>[Inject]</c> members — including
    /// required ones — are populated by <c>InjectServices</c> in a separate step.
    /// </summary>
    private static bool BinderCreateInstanceTakesProvider(CommandModel cmd, bool emitDi, bool emitStandalone)
    {
        if (emitStandalone && !emitDi)
        {
            return false;
        }
        return !cmd.ConstructorParameters.IsEmpty;
    }

    /// <summary>
    /// Emits a thin DI-routed action class. The instance, all bound members, and any
    /// <c>[Inject]</c> values are produced via the umbrella class's three-method
    /// lifecycle: <c>CreateInstance</c> → <c>BindOptions</c> → <c>InjectServices</c>.
    /// When the command declares an instance <c>Configure</c> method, the first two
    /// steps run at <c>ICommandConfigurator.Configure</c> time so the user's hook can
    /// observe bound values; the resulting instance is stashed and reused at
    /// <c>InvokeAsync</c>. The class is emitted nested inside the per-command umbrella
    /// so all binder calls resolve as siblings without a class-name prefix.
    /// </summary>
    private static void EmitDiActionClass(IndentedTextWriter w, CommandModel cmd,
        string className, string methodName, bool acceptsCt,
        ReturnKind returnKind, string? innerTypeFqn,
        bool emitDi, bool emitStandalone)
    {
        var hasConfigure = cmd.ConfigureMethod is not null;
        var instanceConfigure = cmd.ConfigureMethod is { IsStatic: false };
        var callsInjectServices = BinderEmitsInjectServices(cmd, emitDi, emitStandalone);
        var createTakesProvider = BinderCreateInstanceTakesProvider(cmd, emitDi, emitStandalone);
        var header = hasConfigure
            ? $"internal sealed class {className}(Func<IServiceProvider> getServiceProvider) : AsynchronousCommandLineAction, global::triaxis.CommandLine.ICommandConfigurator"
            : $"internal sealed class {className}(Func<IServiceProvider> getServiceProvider) : AsynchronousCommandLineAction";

        w.Block(header, () =>
        {
            if (instanceConfigure)
            {
                // Instance Configure stashes the constructed-and-bound command so
                // InvokeAsync can reuse it (preserving any state the user mutated in
                // Configure). The reset after read keeps the action reusable across
                // separate Run() cycles.
                w.WriteLine($"private {cmd.TypeName}? _configuredInstance;");
                w.WriteLine();
            }

            if (hasConfigure)
            {
                w.Block("public void Configure(global::triaxis.CommandLine.IToolBuilder builder, ParseResult parseResult)", () =>
                {
                    if (instanceConfigure)
                    {
                        w.WriteLine("var instance = CreateInstance(parseResult);");
                        w.WriteLine("BindOptions(instance, parseResult);");
                        EmitConfigureInvocation(w, cmd.ConfigureMethod!);
                        w.WriteLine("_configuredInstance = instance;");
                    }
                    else
                    {
                        EmitConfigureInvocation(w, cmd.ConfigureMethod!);
                    }
                });
                w.WriteLine();
            }

            w.Block("public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)", () =>
            {
                w.WriteLine("var provider = getServiceProvider();");
                w.WriteLine($"var context = new InvocationContext(provider, parseResult, cancellationToken, typeof({cmd.TypeName}));");
                w.WriteLine();
                w.WriteLine("await provider.GetRequiredService<ICommandExecutor>().ExecuteAsync(context, async () =>");
                w.Block(() =>
                {
                    if (instanceConfigure)
                    {
                        // Reuse the configure-phase instance so any user state set in
                        // Configure carries through to Execute. If Configure never ran
                        // (e.g. invocation bypassing ToolBuilder), rebuild here.
                        // Instance Configure forbids ctor DI / required injects, so
                        // CreateInstance never takes a provider in this branch.
                        w.WriteLine("var instance = _configuredInstance;");
                        w.WriteLine("_configuredInstance = null;");
                        w.Block("if (instance is null)", () =>
                        {
                            w.WriteLine("instance = CreateInstance(parseResult);");
                            w.WriteLine("BindOptions(instance, parseResult);");
                        });
                    }
                    else
                    {
                        var createCall = createTakesProvider
                            ? "CreateInstance(parseResult, provider)"
                            : "CreateInstance(parseResult)";
                        w.WriteLine($"var instance = {createCall};");
                        w.WriteLine("BindOptions(instance, parseResult);");
                    }
                    if (callsInjectServices)
                    {
                        w.WriteLine("InjectServices(instance, provider);");
                    }
                    w.WriteLine();
                    EmitDiPathInvocation(w, methodName, acceptsCt, returnKind, innerTypeFqn);
                });
                w.WriteLine(");");
                w.WriteLine();
                w.WriteLine("return context.ExitCode;");
            });
        });
        w.WriteLine();
    }

    /// <summary>
    /// Emits a thin standalone action class. <see cref="IStandaloneAction"/> is the marker
    /// that <see cref="ToolBuilder"/>'s <c>Build()</c> uses to short-circuit to
    /// <see cref="StandaloneHost"/>. Construction goes through <c>CreateInstance</c> +
    /// <c>BindOptions</c>; instance <c>Configure</c> stashes the bound instance for
    /// reuse at invocation time.
    /// </summary>
    private static void EmitStandaloneActionClass(IndentedTextWriter w, CommandModel cmd,
        string className, string methodName,
        bool acceptsToolBuilder, bool acceptsCancellationToken, ReturnKind returnKind,
        bool emitDi, bool emitStandalone)
    {
        var hasConfigure = cmd.ConfigureMethod is not null;
        var instanceConfigure = cmd.ConfigureMethod is { IsStatic: false };
        var callsInjectServices = BinderEmitsInjectServices(cmd, emitDi, emitStandalone);
        var ifaces = hasConfigure
            ? "AsynchronousCommandLineAction, IStandaloneAction, global::triaxis.CommandLine.ICommandConfigurator"
            : "AsynchronousCommandLineAction, IStandaloneAction";
        var header = $"internal sealed class {className} : {ifaces}";

        w.Block(header, () =>
        {
            if (instanceConfigure)
            {
                w.WriteLine($"private {cmd.TypeName}? _configuredInstance;");
                w.WriteLine();
            }

            if (hasConfigure)
            {
                w.Block("public void Configure(global::triaxis.CommandLine.IToolBuilder builder, ParseResult parseResult)", () =>
                {
                    if (instanceConfigure)
                    {
                        w.WriteLine("var instance = CreateInstance(parseResult);");
                        w.WriteLine("BindOptions(instance, parseResult);");
                        EmitConfigureInvocation(w, cmd.ConfigureMethod!);
                        w.WriteLine("_configuredInstance = instance;");
                    }
                    else
                    {
                        EmitConfigureInvocation(w, cmd.ConfigureMethod!);
                    }
                });
                w.WriteLine();
            }

            w.Block("public Task<int> InvokeAsync(IToolBuilder builder, ParseResult parseResult, CancellationToken cancellationToken)", () =>
            {
                w.WriteLine("return InvokeInternalAsync(builder, parseResult, cancellationToken);");
            });
            w.WriteLine();

            // Fallback: invoked via raw System.CommandLine pipeline when no IToolBuilder
            // is available. Methods requiring a builder throw; others run normally.
            w.Block("public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)", () =>
            {
                w.WriteLine("return InvokeInternalAsync(null, parseResult, cancellationToken);");
            });
            w.WriteLine();

            w.Block("private async Task<int> InvokeInternalAsync(IToolBuilder? builder, ParseResult parseResult, CancellationToken cancellationToken)", () =>
            {
                if (acceptsToolBuilder)
                {
                    w.WriteLine("if (builder is null) throw new global::System.InvalidOperationException(" +
                        $"\"Standalone command '{cmd.TypeName}' was invoked without a builder. Use IToolBuilder.Run/RunAsync.\"" +
                        ");");
                    w.WriteLine();
                }
                if (instanceConfigure)
                {
                    w.WriteLine("var instance = _configuredInstance;");
                    w.WriteLine("_configuredInstance = null;");
                    w.Block("if (instance is null)", () =>
                    {
                        w.WriteLine("instance = CreateInstance(parseResult);");
                        w.WriteLine("BindOptions(instance, parseResult);");
                    });
                }
                else
                {
                    w.WriteLine("var instance = CreateInstance(parseResult);");
                    w.WriteLine("BindOptions(instance, parseResult);");
                }
                // Mixed-mode commands (DI primary + standalone action option) share the
                // umbrella's InjectServices helper. The standalone path passes null since
                // it has no provider; assignments short-circuit and the user's standalone
                // entry point is responsible for not reading those members.
                if (callsInjectServices)
                {
                    w.WriteLine("InjectServices(instance, null);");
                }
                w.WriteLine();
                EmitStandaloneInvocation(w, methodName, acceptsToolBuilder,
                    acceptsCancellationToken, returnKind);
            });
        });
        w.WriteLine();
    }

    /// <summary>
    /// Emits a single MainAsync-style invocation in the standalone path. The surrounding
    /// method must return <see cref="int"/>, so each return-shape is mapped to the
    /// appropriate combination of <c>await</c> / <c>return 0</c>. Standalone-routed
    /// <c>[ActionOption]</c> methods may use any of the void/int/Task/Task&lt;int&gt;
    /// shapes; richer return types (collections, <c>ICommandInvocationResult</c>, …)
    /// require the DI path because the standalone host has no <c>InvocationContext</c>
    /// to receive them.
    /// </summary>
    private static void EmitStandaloneInvocation(IndentedTextWriter w, string methodName,
        bool acceptsToolBuilder, bool acceptsCancellationToken, ReturnKind returnKind)
    {
        var callArgs = (acceptsToolBuilder, acceptsCancellationToken) switch
        {
            (true, true) => "builder!, cancellationToken",
            (true, false) => "builder!",
            (false, true) => "cancellationToken",
            (false, false) => "",
        };
        var call = $"instance.{methodName}({callArgs})";

        switch (returnKind)
        {
            case ReturnKind.Void:
                w.WriteLine($"{call};");
                w.WriteLine("return 0;");
                break;

            case ReturnKind.Int:
                w.WriteLine($"return {call};");
                break;

            case ReturnKind.Task:
                w.WriteLine($"await {call};");
                w.WriteLine("return 0;");
                break;

            case ReturnKind.TaskOfInt:
                w.WriteLine($"return await {call};");
                break;

            default:
                // Rich return shapes can't be threaded through the standalone host's
                // bare `Task<int>` contract — surface a helpful error rather than emit
                // code that won't compile. The validation in DetectActionOptions could
                // be extended to flag this at gen time, but the rare case is caught
                // here as a safety net.
                throw new InvalidOperationException(
                    $"Standalone-routed entry point '{methodName}' returns {returnKind}, which is unsupported on the standalone path. Use Task, Task<int>, void, or int.");
        }
    }

    /// <summary>
    /// Emits the minimal accessor declaration needed for a member/segment and returns an
    /// <see cref="Accessor"/> describing how to read/write it inline at call sites.
    /// <paramref name="memberTypeFqn"/> must be the originally declared type (e.g. "int?"
    /// rather than the unwrapped "int") so that the [UnsafeAccessor] signature matches the
    /// actual backing field/property and reflection casts produce the correct value.
    /// </summary>
    private static Accessor EmitAccessor(IndentedTextWriter w, string ownerTypeFqn,
        string memberName, string memberTypeFqn,
        bool isField, bool isPublic, bool hasSetter, bool hasBackingField,
        string identifier, bool hasUnsafeAccessor)
    {
        // Public settable — no accessor declaration needed, access directly.
        if (isPublic && (isField || hasSetter))
        {
            return new Accessor(AccessorKind.Direct, "", memberName, memberTypeFqn);
        }

        // Public read-only or init-only property without a synthesized backing field
        // (i.e. the property has explicit accessor bodies). The field-based path below
        // would emit a reference to <Name>k__BackingField, which the compiler never
        // produced — so go through the property's set_ method via UnsafeAccessor (or
        // reflection). Reads still go through the public getter.
        if (isPublic && !isField && !hasSetter && !hasBackingField)
        {
            if (hasUnsafeAccessor)
            {
                w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Method, Name = {FormatString("set_" + memberName)})]");
                w.WriteLine($"private static extern void {identifier}({ownerTypeFqn} instance, {memberTypeFqn} value);");
                w.WriteLine();
                return new Accessor(AccessorKind.DirectReadUnsafeSetter, identifier, memberName, memberTypeFqn);
            }
            else
            {
                w.WriteLine($"private static readonly PropertyInfo {identifier} = typeof({ownerTypeFqn}).GetProperty({FormatString(memberName)}, BindingFlags.Instance | BindingFlags.Public)!;");
                w.WriteLine();
                return new Accessor(AccessorKind.DirectReadReflectionSetter, identifier, memberName, memberTypeFqn);
            }
        }

        // Public read-only auto-property — read directly via getter, no declaration needed.
        // Write goes through the synthesized backing field.
        if (isPublic && !isField && !hasSetter)
        {
            // Public read-only properties are only used as path segments (read + init-if-null).
            // The init-if-null path is not reachable for a public read-only path segment in the
            // current resolution logic (which throws for public read-only); but to be safe, emit
            // a backing-field setter so writes work. For now, use Direct-read with no setter.
            // We handle the read-only case by emitting only a setter declaration.
            var backingFieldName = "<" + memberName + ">k__BackingField";
            if (hasUnsafeAccessor)
            {
                w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Field, Name = {FormatString(backingFieldName)})]");
                w.WriteLine($"private static extern ref {memberTypeFqn} {identifier}({ownerTypeFqn} instance);");
                w.WriteLine();
                return new Accessor(AccessorKind.UnsafeFieldRef, identifier, memberName, memberTypeFqn);
            }
            else
            {
                w.WriteLine($"private static readonly FieldInfo {identifier} = typeof({ownerTypeFqn}).GetField({FormatString(backingFieldName)}, BindingFlags.Instance | BindingFlags.NonPublic)!;");
                w.WriteLine();
                return new Accessor(AccessorKind.ReflectionField, identifier, memberName, memberTypeFqn);
            }
        }

        // Non-public read-only property — need get_ method (UA) or PropertyInfo (reflection).
        if (!isField && !hasSetter)
        {
            if (hasUnsafeAccessor)
            {
                w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Method, Name = {FormatString("get_" + memberName)})]");
                w.WriteLine($"private static extern {memberTypeFqn} {identifier}({ownerTypeFqn} instance);");
                w.WriteLine();
                return new Accessor(AccessorKind.UnsafeGetter, identifier, memberName, memberTypeFqn);
            }
            else
            {
                w.WriteLine($"private static readonly PropertyInfo {identifier} = typeof({ownerTypeFqn}).GetProperty({FormatString(memberName)}, BindingFlags.Instance | BindingFlags.NonPublic)!;");
                w.WriteLine();
                return new Accessor(AccessorKind.ReflectionProperty, identifier, memberName, memberTypeFqn);
            }
        }

        // Non-public field, or non-public/init-only property with backing field.
        // Properties without a backing field (custom accessors) fall through to a setter-method
        // accessor; reads still go through the (non-public) get_ method.
        if (!isField && !hasBackingField)
        {
            if (hasUnsafeAccessor)
            {
                w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Method, Name = {FormatString("get_" + memberName)})]");
                w.WriteLine($"private static extern {memberTypeFqn} {identifier}_get({ownerTypeFqn} instance);");
                w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Method, Name = {FormatString("set_" + memberName)})]");
                w.WriteLine($"private static extern void {identifier}_set({ownerTypeFqn} instance, {memberTypeFqn} value);");
                w.WriteLine();
                return new Accessor(AccessorKind.UnsafeGetterSetter, identifier, memberName, memberTypeFqn);
            }
            else
            {
                w.WriteLine($"private static readonly PropertyInfo {identifier} = typeof({ownerTypeFqn}).GetProperty({FormatString(memberName)}, BindingFlags.Instance | BindingFlags.NonPublic)!;");
                w.WriteLine();
                return new Accessor(AccessorKind.ReflectionPropertyGetterSetter, identifier, memberName, memberTypeFqn);
            }
        }

        {
            var backingFieldName = isField ? memberName : "<" + memberName + ">k__BackingField";
            if (hasUnsafeAccessor)
            {
                w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Field, Name = {FormatString(backingFieldName)})]");
                w.WriteLine($"private static extern ref {memberTypeFqn} {identifier}({ownerTypeFqn} instance);");
                w.WriteLine();
                return new Accessor(AccessorKind.UnsafeFieldRef, identifier, memberName, memberTypeFqn);
            }
            else
            {
                w.WriteLine($"private static readonly FieldInfo {identifier} = typeof({ownerTypeFqn}).GetField({FormatString(backingFieldName)}, BindingFlags.Instance | BindingFlags.NonPublic)!;");
                w.WriteLine();
                return new Accessor(AccessorKind.ReflectionField, identifier, memberName, memberTypeFqn);
            }
        }
    }

    private static string FormatRead(Accessor a, string target) => a.Kind switch
    {
        AccessorKind.Direct => $"{target}.{a.MemberName}",
        AccessorKind.DirectReadUnsafeSetter => $"{target}.{a.MemberName}",
        AccessorKind.DirectReadReflectionSetter => $"{target}.{a.MemberName}",
        AccessorKind.UnsafeFieldRef => $"{a.Identifier}({target})",
        AccessorKind.UnsafeGetter => $"{a.Identifier}({target})",
        AccessorKind.UnsafeGetterSetter => $"{a.Identifier}_get({target})",
        AccessorKind.ReflectionField => $"({a.MemberTypeFqn}){a.Identifier}.GetValue({target})!",
        AccessorKind.ReflectionProperty => $"({a.MemberTypeFqn}){a.Identifier}.GetValue({target})!",
        AccessorKind.ReflectionPropertyGetterSetter => $"({a.MemberTypeFqn}){a.Identifier}.GetValue({target})!",
        _ => throw new InvalidOperationException(),
    };

    private static string FormatWrite(Accessor a, string target, string valueExpr) => a.Kind switch
    {
        AccessorKind.Direct => $"{target}.{a.MemberName} = {valueExpr}",
        AccessorKind.DirectReadUnsafeSetter => $"{a.Identifier}({target}, {valueExpr})",
        AccessorKind.DirectReadReflectionSetter => $"{a.Identifier}.SetValue({target}, {valueExpr})",
        AccessorKind.UnsafeFieldRef => $"{a.Identifier}({target}) = {valueExpr}",
        AccessorKind.UnsafeGetterSetter => $"{a.Identifier}_set({target}, {valueExpr})",
        AccessorKind.ReflectionField => $"{a.Identifier}.SetValue({target}, {valueExpr})",
        AccessorKind.ReflectionPropertyGetterSetter => $"{a.Identifier}.SetValue({target}, {valueExpr})",
        _ => throw new InvalidOperationException($"Cannot write to accessor of kind {a.Kind}"),
    };

    private static string MemberKey(MemberModel m) =>
        string.Join(".", m.AccessPath.Select(s => s.MemberName).Append(m.MemberName));

    private static void GenerateExecuteCall(IndentedTextWriter w, CommandModel cmd, string? cancellationTokenArg)
    {
        var exec = cmd.ExecuteMethod;
        var callArgs = cancellationTokenArg is not null ? cancellationTokenArg : "";
        EmitMethodCallExpression(w, exec.MethodName, exec.ReturnKind, exec.InnerTypeFqn, callArgs);
    }

    /// <summary>
    /// Emits the "execute on the DI/middleware path" invocation for a method on the
    /// command instance — primary entry point or an <c>[ActionOption]</c> method.
    /// Methods that don't accept a <c>CancellationToken</c> are wrapped in a
    /// <c>FailFast</c> registration so process termination still tears down the host
    /// even though the method body itself is uncancellable.
    /// </summary>
    private static void EmitDiPathInvocation(IndentedTextWriter w, string methodName,
        bool acceptsCancellationToken, ReturnKind returnKind, string? innerTypeFqn)
    {
        if (acceptsCancellationToken)
        {
            EmitMethodCallExpression(w, methodName, returnKind, innerTypeFqn, "context.GetCancellationToken()");
        }
        else
        {
            w.WriteLine("var failFastRegistration = context.GetCancellationToken().Register(static () => Environment.FailFast(null));");
            w.Block("try", () =>
            {
                EmitMethodCallExpression(w, methodName, returnKind, innerTypeFqn, "");
            });
            w.Block("finally", () =>
            {
                w.WriteLine("failFastRegistration.Dispose();");
            });
        }
    }

    private static void EmitMethodCallExpression(IndentedTextWriter w, string methodName, ReturnKind returnKind, string? innerTypeFqn, string callArgs)
    {
        var call = $"instance.{methodName}({callArgs})";

        switch (returnKind)
        {
            case ReturnKind.Void:
                w.WriteLine($"{call};");
                break;

            case ReturnKind.Task:
                w.WriteLine($"await {call};");
                break;

            case ReturnKind.Int:
                w.WriteLine($"context.ExitCode = {call};");
                break;

            case ReturnKind.TaskOfInt:
                w.WriteLine($"context.ExitCode = await {call};");
                break;

            case ReturnKind.TaskOfICommandInvocationResult:
                w.WriteLine($"context.InvocationResult = await {call};");
                break;

            case ReturnKind.ICommandInvocationResult:
                w.WriteLine($"context.InvocationResult = {call};");
                break;

            case ReturnKind.TaskOfIEnumerableOfT:
                w.WriteLine($"context.InvocationResult = new AsyncIEnumerableCommandInvocationResult<{innerTypeFqn}>({call});");
                break;

            case ReturnKind.TaskOfT:
                w.WriteLine($"context.InvocationResult = new AsyncValueCommandInvocationResult<{innerTypeFqn}>({call});");
                break;

            case ReturnKind.IAsyncEnumerableOfT:
                w.WriteLine($"context.InvocationResult = new AsyncEnumerableCommandInvocationResult<{innerTypeFqn}>({call});");
                break;

            case ReturnKind.IEnumerableOfT:
                w.WriteLine($"context.InvocationResult = new EnumerableCommandInvocationResult<{innerTypeFqn}>({call});");
                break;

            case ReturnKind.Value:
                w.WriteLine($"context.InvocationResult = new ValueCommandInvocationResult<{innerTypeFqn}>({call});");
                break;
        }
    }

    private static string FormatMemberAssignment(MemberModel member, string valueExpr, Accessor accessor)
    {
        var target = member.AccessPath.Length == 0
            ? "instance"
            : "__opts_" + string.Join("_", member.AccessPath.Select(s => s.MemberName));

        return FormatWrite(accessor, target, valueExpr) + ";";
    }

    /// <summary>
    /// Generates eager resolution of all [Options] path prefixes used by arguments/options.
    /// Called once after instance creation, before any binding.
    /// </summary>
    /// <summary>
    /// Builds a new-expression for an [Options] type. Required children and required
    /// nested [Options] are included in the initializer as <c>default!</c> placeholders
    /// to satisfy the <c>required</c> modifier — the real values are assigned later
    /// through the regular parseResult bind loop (see <see cref="GenerateOptionsPathResolution"/>).
    /// </summary>
    private static string FormatOptionsCreateExpr(AccessPathSegment seg, AccessPathSegment[] prefix, int depth, MemberModel[] allMembers)
    {
        var requiredChildren = allMembers
            .Where(m => m.AccessPath.Length == depth
                && m.NeedsInitializer
                && m.AccessPath.Take(depth).Select(s => s.MemberName).SequenceEqual(prefix.Select(s => s.MemberName)))
            .ToArray();
        var requiredNestedOpts = allMembers
            .Where(m => m.AccessPath.Length > depth
                && m.AccessPath[depth].IsMemberRequired
                && m.AccessPath.Take(depth).Select(s => s.MemberName).SequenceEqual(prefix.Select(s => s.MemberName)))
            .Select(m => m.AccessPath[depth])
            .Distinct()
            .ToArray();

        var initParts = requiredChildren
            .Select(m => $"{m.MemberName} = default!")
            .Concat(requiredNestedOpts.Select(s => $"{s.MemberName} = null!"))
            .ToArray();

        return initParts.Length > 0
            ? $"new {seg.MemberTypeFqn} {{ {string.Join(", ", initParts)} }}"
            : $"new {seg.MemberTypeFqn}()";
    }

    private static void GenerateOptionsPathResolution(IndentedTextWriter w, MemberModel[] args, MemberModel[] opts, Dictionary<string, Accessor> pathAccessors, Dictionary<string, Accessor> memberAccessors)
    {
        var allMembers = args.Concat(opts).ToArray();
        var resolved = new HashSet<string>();
        foreach (var member in allMembers)
        {
            for (var depth = 1; depth <= member.AccessPath.Length; depth++)
            {
                var prefix = member.AccessPath.Take(depth).ToArray();
                var key = string.Join("_", prefix.Select(s => s.MemberName));
                if (!resolved.Add(key))
                {
                    continue;
                }

                var seg = prefix[depth - 1];
                var varName = $"__opts_{key}";
                var parentVar = depth == 1
                    ? "instance"
                    : "__opts_" + string.Join("_", prefix.Take(depth - 1).Select(s => s.MemberName));

                var accessor = pathAccessors[seg.OwnerTypeFqn + "." + seg.MemberName];

                var createExpr = FormatOptionsCreateExpr(seg, prefix, depth, allMembers);
                var readExpr = FormatRead(accessor, parentVar);

                if (seg.IsField || seg.HasSetter)
                {
                    // Settable: read, create-if-null, assign back
                    if (accessor.Kind == AccessorKind.Direct)
                    {
                        w.WriteLine($"var {varName} = {readExpr} ?? ({FormatWrite(accessor, parentVar, createExpr)});");
                    }
                    else
                    {
                        w.WriteLine($"var {varName} = {readExpr};");
                        w.Block($"if ({varName} is null)", () =>
                        {
                            w.WriteLine($"{FormatWrite(accessor, parentVar, varName + " = " + createExpr)};");
                        });
                    }
                }
                else
                {
                    // Read-only: expect pre-initialized
                    w.WriteLine($"var {varName} = {readExpr} ?? throw new InvalidOperationException(\"[Options] property '{seg.MemberName}' returned null but has no setter\");");
                }

                // Prime required members at this depth from the parse result. This covers
                // the case where the [Options] container was pre-initialized by the user
                // (so the `default!` placeholders in the create expression above never ran)
                // and also ensures missing optional CLI values fall through to the option's
                // declared default via `parseResult.GetValue<T>(name)`.
                var primeMembers = allMembers
                    .Where(m => m.AccessPath.Length == depth
                        && m.NeedsInitializer
                        && m.AccessPath.Select(s => s.MemberName).SequenceEqual(prefix.Select(s => s.MemberName)))
                    .ToArray();
                foreach (var prim in primeMembers)
                {
                    var primAccessor = memberAccessors[MemberKey(prim)];
                    w.WriteLine(FormatWrite(primAccessor, varName, $"parseResult.GetValue<{prim.MemberTypeFqn}>({FormatString(GetCliName(prim))})") + ";");
                }
            }
        }
    }

    private static MemberModel[] GetArguments(CommandModel cmd) =>
        OrderWithinGroups(cmd.Members.Where(m => m.Kind == MemberKind.Argument));

    private static MemberModel[] GetOptions(CommandModel cmd) =>
        OrderWithinGroups(cmd.Members.Where(m => m.Kind == MemberKind.Option));

    /// <summary>
    /// Sorts members by <see cref="MemberModel.Order"/> within contiguous runs
    /// that share the same access path, preserving the relative position of
    /// different groups (direct members vs. each [Options] block).
    /// </summary>
    private static MemberModel[] OrderWithinGroups(IEnumerable<MemberModel> members)
    {
        var result = new List<MemberModel>();
        var group = new List<MemberModel>();
        AccessPathSegment[]? groupPath = null;

        foreach (var m in members)
        {
            if (group.Count > 0 && !AccessPathEquals(m.AccessPath, groupPath!))
            {
                FlushGroup(result, group);
            }

            groupPath = m.AccessPath;
            group.Add(m);
        }

        FlushGroup(result, group);
        return result.ToArray();

        static void FlushGroup(List<MemberModel> result, List<MemberModel> group)
        {
            if (group.Count == 0)
            {
                return;
            }
            group.Sort((a, b) => a.Order.CompareTo(b.Order));
            result.AddRange(group);
            group.Clear();
        }

        static bool AccessPathEquals(AccessPathSegment[] a, AccessPathSegment[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i].MemberName != b[i].MemberName || a[i].OwnerTypeFqn != b[i].OwnerTypeFqn)
                {
                    return false;
                }
            }
            return true;
        }
    }

    private static string GetSafeName(CommandModel cmd)
    {
        // Path is empty for a root-level command (no name on [Command]). The umbrella
        // class needs a non-empty identifier, so fall back to a stable placeholder.
        // Path segments are PascalCased so the resulting type name reads well at use
        // sites (`Greet.Action`, `Foo_Bar.Action`) and dodges CS8981 (all-lowercase
        // identifiers may become language-reserved).
        if (cmd.Path.Length == 0)
        {
            return "_root";
        }
        var segments = cmd.Path.Select(p => Capitalize(p.Replace("-", "_")));
        return string.Join("_", segments);

        static string Capitalize(string s)
            => s.Length == 0 || char.IsUpper(s[0]) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    private static string GetCliName(MemberModel member)
    {
        if (member.Name is not null)
        {
            return member.Name;
        }

        var name = member.MemberName.TrimStart('_');
        if (member.Kind == MemberKind.Argument)
        {
            return ToKebabCase(name, upper: true);
        }
        return (name.Length == 1 ? "-" : "--") + ToKebabCase(name, upper: false);
    }

    /// <summary>
    /// Converts a PascalCase / camelCase identifier to kebab-case. Inserts a hyphen
    /// at lower→upper boundaries and between an acronym run and a following word
    /// (e.g. <c>ParseHTMLDocument</c> → <c>parse-html-document</c>). When
    /// <paramref name="upper"/> is true, the result is uppercased instead
    /// (SCREAMING-KEBAB, used for argument names).
    /// </summary>
    private static string ToKebabCase(string value, bool upper)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sb = new System.Text.StringBuilder(value.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i > 0 && char.IsUpper(c))
            {
                var prev = value[i - 1];
                var nextLower = i + 1 < value.Length && char.IsLower(value[i + 1]);
                if ((!char.IsUpper(prev) && prev != '-') || (char.IsUpper(prev) && nextLower))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                    {
                        sb.Append('-');
                    }
                }
            }
            sb.Append(upper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string GetActionOptionCliName(ActionOptionModel ao)
    {
        if (ao.Name is not null)
        {
            return ao.Name;
        }
        var name = ao.MethodName.TrimStart('_');
        if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5)
        {
            name = name.Substring(0, name.Length - 5);
        }
        return (name.Length == 1 ? "-" : "--") + ToKebabCase(name, upper: false);
    }

    private static string GetMemberFieldName(MemberModel member)
    {
        if (member.AccessPath.Length > 0)
        {
            return string.Join("_", member.AccessPath.Select(s => s.MemberName)) + "_" + member.MemberName;
        }
        return member.MemberName;
    }

    private static string FormatString(string value)
    {
        return SyntaxFactory.Literal(value).ToFullString();
    }

    private static string FormatStringArray(string[] values)
    {
        if (values.Length == 0)
        {
            return "";
        }
        return string.Join(", ", values.Select(FormatString));
    }

    private static string FormatStringArrayInline(string[] values)
    {
        return "new[] { " + string.Join(", ", values.Select(FormatString)) + " }";
    }
}

enum MemberKind
{
    Argument,
    Option,
    Inject,
}

enum AccessorKind
{
    /// <summary>Public settable: access directly via <c>instance.Member</c>.</summary>
    Direct,
    /// <summary>Public init-only/read-only with no backing field: read direct, write via an [UnsafeAccessor] set_ method.</summary>
    DirectReadUnsafeSetter,
    /// <summary>Public init-only/read-only with no backing field: read direct, write via a cached PropertyInfo (reflection fallback).</summary>
    DirectReadReflectionSetter,
    /// <summary>[UnsafeAccessor] field ref return: <c>method(instance)</c> yields a ref.</summary>
    UnsafeFieldRef,
    /// <summary>[UnsafeAccessor] get_ method: <c>method(instance)</c> reads via property getter.</summary>
    UnsafeGetter,
    /// <summary>[UnsafeAccessor] get_/set_ method pair for non-public properties with no backing field.</summary>
    UnsafeGetterSetter,
    /// <summary>Cached <see cref="System.Reflection.FieldInfo"/> used for GetValue/SetValue.</summary>
    ReflectionField,
    /// <summary>Cached <see cref="System.Reflection.PropertyInfo"/> used for GetValue.</summary>
    ReflectionProperty,
    /// <summary>Cached <see cref="System.Reflection.PropertyInfo"/> used for GetValue/SetValue on non-public properties with no backing field.</summary>
    ReflectionPropertyGetterSetter,
}

record Accessor(AccessorKind Kind, string Identifier, string MemberName, string MemberTypeFqn);

enum ReturnKind
{
    Void,
    Task,
    Int,
    TaskOfInt,
    TaskOfICommandInvocationResult,
    TaskOfIEnumerableOfT,
    TaskOfT,
    IAsyncEnumerableOfT,
    IEnumerableOfT,
    ICommandInvocationResult,
    Value,
}

record ExecuteMethodModel(
    string MethodName,
    bool IsAsync,
    bool AcceptsCancellationToken,
    string ReturnTypeFqn,
    ReturnKind ReturnKind,
    string? InnerTypeFqn,
    bool AcceptsToolBuilder = false);

record CommandModel(
    string TypeName,
    string[] Path,
    string? Description,
    string[]? Aliases,
    string[]? SupportedPlatforms,
    ImmutableArray<MemberModel> Members,
    ExecuteMethodModel ExecuteMethod,
    ImmutableArray<ConstructorParameterModel> ConstructorParameters,
    bool IsStandalone = false,
    ConfigureMethodModel? ConfigureMethod = null,
    ImmutableArray<ActionOptionModel> ActionOptions = default,
    ImmutableArray<string> Diagnostics = default);

record ActionOptionModel(
    string MethodName,
    string? Name,
    string? Description,
    string[]? Aliases,
    double Order,
    bool AcceptsToolBuilder,
    bool AcceptsCancellationToken,
    string ReturnTypeFqn,
    ReturnKind ReturnKind,
    string? InnerTypeFqn);

[Flags]
enum ConfigureParamKind
{
    None = 0,
    ToolBuilder = 1,
    HostBuilder = 2,
    ServiceCollection = 4,
}

record ConfigureMethodModel(
    string DeclaringTypeFqn,
    ImmutableArray<ConfigureParamKind> Parameters,
    bool IsStatic);

record AssemblyCommandModel(
    string[] Path,
    string? Description,
    string[]? Aliases);

record EntryPointModel(
    bool HasToolPackage,
    string? ConfigOverridePath,
    string? EnvironmentVariablePrefix,
    bool ProducesOutput,
    ImmutableArray<ConfigureServicesHookModel> ConfigureServicesHooks);

record ConfigureServicesHookModel(
    string DeclaringTypeFqn,
    string MethodName);

record AccessPathSegment(
    string MemberName,
    string MemberTypeFqn,
    string DeclaredTypeFqn,
    bool IsField,
    bool IsPublic,
    bool HasSetter,
    bool HasBackingField,
    bool IsMemberRequired,
    string OwnerTypeFqn);

record MemberModel(
    MemberKind Kind,
    string MemberName,
    string MemberTypeFqn,
    string DeclaredTypeFqn,
    bool IsField,
    bool IsPublic,
    bool HasSetter,
    bool IsInitOnly,
    bool HasBackingField,
    string DeclaringTypeFqn,
    string? Name,
    string? Description,
    string[]? Aliases,
    bool? Required,
    bool IsMemberRequired,
    bool IsCollection,
    bool IsNullable,
    double Order,
    string? InjectTypeFqn,
    AccessPathSegment[] AccessPath)
{
    /// <summary>True when the member must be set in the object initializer (C# required keyword).</summary>
    public bool NeedsInitializer => IsMemberRequired;
}

record ConstructorParameterModel(
    string TypeFqn);

static class IndentedTextWriterExtensions
{
    public static void Block(this IndentedTextWriter w, Action body, string suffix = "", bool eol = true)
    {
        w.WriteLine("{");
        w.Indent++;
        body();
        w.Indent--;
        if (eol)
        {
            w.WriteLine("}" + suffix);
        }
        else
        {
            w.Write("}" + suffix);
        }
    }

    public static void Block(this IndentedTextWriter w, string header, Action body, string suffix = "", bool eol = true)
    {
        w.WriteLine(header);
        w.Block(body, suffix, eol);
    }
}
