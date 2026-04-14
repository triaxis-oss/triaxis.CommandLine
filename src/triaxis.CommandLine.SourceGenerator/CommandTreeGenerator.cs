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

        var entryPointModel = context.CompilationProvider
            .Combine(entryPointProps)
            .Combine(collected)
            .Select(static (pair, ct) =>
            {
                var ((compilation, props), commands) = pair;
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

                return new EntryPointModel(hasToolPackage,
                    hasToolPackage ? props.ConfigOverridePath : null,
                    hasToolPackage ? props.EnvironmentVariablePrefix : null,
                    producesOutput);
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
                        w.WriteLine(".Run();");
                        w.Indent--;
                    }
                    else
                    {
                        w.WriteLine("return global::triaxis.CommandLine.Tool.CreateBuilder(args).AddCommandsFromAssembly(typeof(GeneratedProgram).Assembly).Run();");
                    }
                });
            });
        });

        w.Flush();
        return sw.ToString();
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

        var executeMethod = DetectExecuteMethod(typeSymbol);
        var ctorParams = ExtractConstructorParameters(typeSymbol);

        return new CommandModel(
            typeSymbol.ToDisplayString(FqnFormat),
            path!,
            description,
            aliases,
            supportedPlatforms,
            members.ToImmutableArray(),
            executeMethod,
            ctorParams);
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

    private static ExecuteMethodModel DetectExecuteMethod(INamedTypeSymbol typeSymbol)
    {
        // Check ExecuteAsync(CancellationToken) first
        foreach (var m in typeSymbol.GetMembers("ExecuteAsync").OfType<IMethodSymbol>())
        {
            if (m.Parameters.Length == 1 &&
                m.Parameters[0].Type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                var (kind, innerType) = AnalyzeReturnType(m.ReturnType);
                return new ExecuteMethodModel("ExecuteAsync", true, true,
                    m.ReturnType.ToDisplayString(FqnFormat),
                    kind, innerType);
            }
        }

        // Check ExecuteAsync()
        foreach (var m in typeSymbol.GetMembers("ExecuteAsync").OfType<IMethodSymbol>())
        {
            if (m.Parameters.Length == 0)
            {
                var (kind, innerType) = AnalyzeReturnType(m.ReturnType);
                return new ExecuteMethodModel("ExecuteAsync", true, false,
                    m.ReturnType.ToDisplayString(FqnFormat),
                    kind, innerType);
            }
        }

        // Check Execute()
        foreach (var m in typeSymbol.GetMembers("Execute").OfType<IMethodSymbol>())
        {
            if (m.Parameters.Length == 0)
            {
                var (kind, innerType) = AnalyzeReturnType(m.ReturnType);
                return new ExecuteMethodModel("Execute", false, false,
                    m.ReturnType.ToDisplayString(FqnFormat),
                    kind, innerType);
            }
        }

        return new ExecuteMethodModel("ExecuteAsync", true, false, "global::System.Threading.Tasks.Task", ReturnKind.Task, null);
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
                            MemberKind.Argument, member.Name, memberTypeFqn, isField, isPublic, hasSetter, isInitOnly,
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
                            MemberKind.Option, member.Name, memberTypeFqn, isField, isPublic, hasSetter, isInitOnly,
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
                                member.Name, memberTypeFqn, isField, isPublic, hasSetter, isMemberRequired, declaringTypeFqn);
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
                            MemberKind.Inject, member.Name, memberTypeFqn, isField, isPublic, hasSetter, isInitOnly,
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

            // Per-command action classes
            foreach (var cmd in commands)
            {
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

            if (node.Command is { } cmd)
            {
                var safeName = GetSafeName(cmd);
                var args = GetArguments(cmd);
                var opts = GetOptions(cmd);

                w.WriteLine($"Action = new {safeName}_Action(getServiceProvider),");
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
                if (opts.Length > 0)
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
        var safeName = GetSafeName(cmd);
        var args = GetArguments(cmd);
        var opts = GetOptions(cmd);
        var injects = cmd.Members.Where(m => m.Kind == MemberKind.Inject).ToArray();
        var nonPublicDirect = args.Concat(opts).Concat(injects).Where(m => !m.IsPublic && m.AccessPath.Length == 0).ToArray();
        var exec = cmd.ExecuteMethod;

        w.Block($"internal sealed class {safeName}_Action(Func<IServiceProvider> getServiceProvider) : AsynchronousCommandLineAction", () =>
        {

        // Accessors for direct members that need backing field access
        // (non-public members, or public read-only properties)
        // Skip required members — they are set in the object initializer.
        var memberAccessors = new Dictionary<string, Accessor>();
        foreach (var member in args.Concat(opts).Concat(injects).Where(m => m.AccessPath.Length == 0 && !m.NeedsInitializer))
        {
            var owner = member.DeclaringTypeFqn;
            memberAccessors[MemberKey(member)] = EmitAccessor(
                w, owner, member.MemberName, member.MemberTypeFqn,
                member.IsField, member.IsPublic, member.HasSetter,
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
                    w, seg.OwnerTypeFqn, seg.MemberName, seg.MemberTypeFqn,
                    seg.IsField, seg.IsPublic, seg.HasSetter,
                    $"__access_path_{seg.MemberName}", hasUnsafeAccessor);
            }
        }

        // Accessors for members on nested [Options] types that need backing field access
        foreach (var member in args.Concat(opts).Where(m => m.AccessPath.Length > 0))
        {
            var lastSeg = member.AccessPath[member.AccessPath.Length - 1];
            memberAccessors[MemberKey(member)] = EmitAccessor(
                w, lastSeg.MemberTypeFqn, member.MemberName, member.MemberTypeFqn,
                member.IsField, member.IsPublic, member.HasSetter,
                $"__access_{GetMemberFieldName(member)}", hasUnsafeAccessor);
        }

        // InvokeAsync
        w.Block("public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)", () =>
        {
            w.WriteLine("var provider = getServiceProvider();");
            w.WriteLine($"var context = new InvocationContext(provider, parseResult, cancellationToken, typeof({cmd.TypeName}));");
            w.WriteLine();
            w.WriteLine("await provider.GetRequiredService<ICommandExecutor>().ExecuteAsync(context, async () =>");
            w.Block(() =>
            {
                // Identify required direct members that must go in the object initializer
                var requiredDirectArgs = args.Where(m => m.NeedsInitializer && m.AccessPath.Length == 0).ToArray();
                var requiredDirectOpts = opts.Where(m => m.NeedsInitializer && m.AccessPath.Length == 0).ToArray();
                var requiredDirectInjects = injects.Where(m => m.NeedsInitializer).ToArray();
                // Required [Options] properties on the command itself
                var requiredOptionsProps = args.Concat(opts)
                    .Where(m => m.AccessPath.Length > 0 && m.AccessPath[0].IsMemberRequired)
                    .Select(m => m.AccessPath[0])
                    .Distinct()
                    .ToArray();

                // Create instance — required/init members go in the object initializer
                var hasInitializer = requiredDirectArgs.Length > 0 || requiredDirectOpts.Length > 0
                    || requiredDirectInjects.Length > 0 || requiredOptionsProps.Length > 0;

                var ctorExpr = cmd.ConstructorParameters.IsEmpty
                    ? $"new {cmd.TypeName}()"
                    : $"new {cmd.TypeName}({string.Join(", ", cmd.ConstructorParameters.Select(p => $"provider.GetRequiredService<{p.TypeFqn}>()"))})";

                if (hasInitializer)
                {
                    var initExpr = cmd.ConstructorParameters.IsEmpty
                        ? $"new {cmd.TypeName}"
                        : ctorExpr;
                    w.Block($"var instance = {initExpr}", () =>
                    {
                        foreach (var member in requiredDirectArgs.Cast<MemberModel>().Concat(requiredDirectOpts))
                        {
                            w.WriteLine($"{member.MemberName} = parseResult.GetValue<{member.MemberTypeFqn}>({FormatString(GetCliName(member))}),");
                        }
                        foreach (var inject in requiredDirectInjects)
                        {
                            var serviceType = inject.InjectTypeFqn ?? inject.MemberTypeFqn;
                            w.WriteLine($"{inject.MemberName} = provider.GetRequiredService<{serviceType}>(),");
                        }
                        foreach (var seg in requiredOptionsProps)
                        {
                            var prefix = new[] { seg };
                            var allMembers = args.Concat(opts).ToArray();
                            w.WriteLine($"{seg.MemberName} = {FormatOptionsCreateExpr(seg, prefix, 1, allMembers)},");
                        }
                    }, suffix: ";");
                }
                else
                {
                    w.WriteLine($"var instance = {ctorExpr};");
                }
                w.WriteLine();

                // Direct [Inject] assignments (skip required members already set in initializer)
                foreach (var inject in injects.Where(i => !i.NeedsInitializer))
                {
                    var serviceType = inject.InjectTypeFqn ?? inject.MemberTypeFqn;
                    w.WriteLine(FormatWrite(memberAccessors[MemberKey(inject)], "instance", $"provider.GetRequiredService<{serviceType}>()") + ";");
                }

                // Eagerly resolve [Options] nested objects
                GenerateOptionsPathResolution(w, args, opts, pathAccessors);

                // Bind explicit argument/option values from parse results.
                // Members inside an [Options] container are always bound here — even if
                // `required`/`init` — because the container may be pre-initialized by the
                // user, in which case the object initializer in the null-branch of the
                // container's path resolution never runs. Their accessors write through
                // the backing field, so init-only members can be set post-construction.
                var bindableArgs = args.Where(a => !a.NeedsInitializer || a.AccessPath.Length > 0).ToArray();
                var bindableOpts = opts.Where(o => !o.NeedsInitializer || o.AccessPath.Length > 0).ToArray();

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
                    w.WriteLine();
                }

                // Execute
                if (exec.AcceptsCancellationToken)
                {
                    GenerateExecuteCall(w, cmd, "context.GetCancellationToken()");
                }
                else
                {
                    w.WriteLine("var failFastRegistration = context.GetCancellationToken().Register(static () => Environment.FailFast(null));");
                    w.Block("try", () =>
                    {
                        GenerateExecuteCall(w, cmd, null);
                    });
                    w.Block("finally", () =>
                    {
                        w.WriteLine("failFastRegistration.Dispose();");
                    });
                }
            });
            w.WriteLine(");");
            w.WriteLine();
            w.WriteLine("return context.ExitCode;");
        });

        }); // end class block
        w.WriteLine();
    }

    /// <summary>
    /// Emits the minimal accessor declaration needed for a member/segment and returns an
    /// <see cref="Accessor"/> describing how to read/write it inline at call sites.
    /// </summary>
    private static Accessor EmitAccessor(IndentedTextWriter w, string ownerTypeFqn,
        string memberName, string memberTypeFqn,
        bool isField, bool isPublic, bool hasSetter,
        string identifier, bool hasUnsafeAccessor)
    {
        // Public settable — no accessor declaration needed, access directly.
        if (isPublic && (isField || hasSetter))
        {
            return new Accessor(AccessorKind.Direct, "", memberName, memberTypeFqn);
        }

        // Public read-only property — read directly via getter, no declaration needed.
        // Write still requires a backing-field accessor (emitted below).
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
        AccessorKind.UnsafeFieldRef => $"{a.Identifier}({target})",
        AccessorKind.UnsafeGetter => $"{a.Identifier}({target})",
        AccessorKind.ReflectionField => $"({a.MemberTypeFqn}){a.Identifier}.GetValue({target})!",
        AccessorKind.ReflectionProperty => $"({a.MemberTypeFqn}){a.Identifier}.GetValue({target})!",
        _ => throw new InvalidOperationException(),
    };

    private static string FormatWrite(Accessor a, string target, string valueExpr) => a.Kind switch
    {
        AccessorKind.Direct => $"{target}.{a.MemberName} = {valueExpr}",
        AccessorKind.UnsafeFieldRef => $"{a.Identifier}({target}) = {valueExpr}",
        AccessorKind.ReflectionField => $"{a.Identifier}.SetValue({target}, {valueExpr})",
        _ => throw new InvalidOperationException($"Cannot write to accessor of kind {a.Kind}"),
    };

    private static string MemberKey(MemberModel m) =>
        string.Join(".", m.AccessPath.Select(s => s.MemberName).Append(m.MemberName));

    private static void GenerateExecuteCall(IndentedTextWriter w, CommandModel cmd, string? cancellationTokenArg)
    {
        var exec = cmd.ExecuteMethod;
        var callArgs = cancellationTokenArg is not null ? cancellationTokenArg : "";
        var call = $"instance.{exec.MethodName}({callArgs})";

        switch (exec.ReturnKind)
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
                w.WriteLine($"context.InvocationResult = new AsyncIEnumerableCommandInvocationResult<{exec.InnerTypeFqn}>({call});");
                break;

            case ReturnKind.TaskOfT:
                w.WriteLine($"context.InvocationResult = new AsyncValueCommandInvocationResult<{exec.InnerTypeFqn}>({call});");
                break;

            case ReturnKind.IAsyncEnumerableOfT:
                w.WriteLine($"context.InvocationResult = new AsyncEnumerableCommandInvocationResult<{exec.InnerTypeFqn}>({call});");
                break;

            case ReturnKind.IEnumerableOfT:
                w.WriteLine($"context.InvocationResult = new EnumerableCommandInvocationResult<{exec.InnerTypeFqn}>({call});");
                break;

            case ReturnKind.Value:
                w.WriteLine($"context.InvocationResult = new ValueCommandInvocationResult<{exec.InnerTypeFqn}>({call});");
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
    /// Builds a new-expression for an [Options] type, including required children
    /// and required nested [Options] in the initializer.
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
            .Select(m => $"{m.MemberName} = parseResult.GetValue<{m.MemberTypeFqn}>({FormatString(GetCliName(m))})")
            .Concat(requiredNestedOpts.Select(s => $"{s.MemberName} = null!"))
            .ToArray();

        return initParts.Length > 0
            ? $"new {seg.MemberTypeFqn} {{ {string.Join(", ", initParts)} }}"
            : $"new {seg.MemberTypeFqn}()";
    }

    private static void GenerateOptionsPathResolution(IndentedTextWriter w, MemberModel[] args, MemberModel[] opts, Dictionary<string, Accessor> pathAccessors)
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
        return string.Join("_", cmd.Path).Replace("-", "_");
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
            return name.ToUpperInvariant();
        }
        return (name.Length == 1 ? "-" : "--") + name;
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
    /// <summary>[UnsafeAccessor] field ref return: <c>method(instance)</c> yields a ref.</summary>
    UnsafeFieldRef,
    /// <summary>[UnsafeAccessor] get_ method: <c>method(instance)</c> reads via property getter.</summary>
    UnsafeGetter,
    /// <summary>Cached <see cref="System.Reflection.FieldInfo"/> used for GetValue/SetValue.</summary>
    ReflectionField,
    /// <summary>Cached <see cref="System.Reflection.PropertyInfo"/> used for GetValue.</summary>
    ReflectionProperty,
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
    string? InnerTypeFqn);

record CommandModel(
    string TypeName,
    string[] Path,
    string? Description,
    string[]? Aliases,
    string[]? SupportedPlatforms,
    ImmutableArray<MemberModel> Members,
    ExecuteMethodModel ExecuteMethod,
    ImmutableArray<ConstructorParameterModel> ConstructorParameters);

record AssemblyCommandModel(
    string[] Path,
    string? Description,
    string[]? Aliases);

record EntryPointModel(
    bool HasToolPackage,
    string? ConfigOverridePath,
    string? EnvironmentVariablePrefix,
    bool ProducesOutput);

record AccessPathSegment(
    string MemberName,
    string MemberTypeFqn,
    bool IsField,
    bool IsPublic,
    bool HasSetter,
    bool IsMemberRequired,
    string OwnerTypeFqn);

record MemberModel(
    MemberKind Kind,
    string MemberName,
    string MemberTypeFqn,
    bool IsField,
    bool IsPublic,
    bool HasSetter,
    bool IsInitOnly,
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
