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
        var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "");
        var combined = collected.Combine(assemblyName);

        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var (commands, asmName) = pair;
            if (commands.IsDefaultOrEmpty)
            {
                return;
            }

            var source = GenerateSource(commands, asmName);
            spc.AddSource("GeneratedCommandTree.g.cs", source);
        });
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

        var members = new List<MemberModel>();
        CollectMembers(typeSymbol, members, ct);

        var executeMethod = DetectExecuteMethod(typeSymbol);

        return new CommandModel(
            typeSymbol.ToDisplayString(FqnFormat),
            path!,
            description,
            aliases,
            members.ToImmutableArray(),
            executeMethod);
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

                if (memberType is INamedTypeSymbol nts &&
                    nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                    nts.TypeArguments.Length == 1)
                {
                    memberType = nts.TypeArguments[0];
                }

                var memberTypeFqn = memberType.ToDisplayString(FqnFormat);
                var isField = member is IFieldSymbol;
                var isPublic = member.DeclaredAccessibility == Accessibility.Public;
                var hasSetter = member is IFieldSymbol || (member is IPropertySymbol prop && prop.SetMethod is { IsInitOnly: false });
                var declaringTypeFqn = member.ContainingType.ToDisplayString(FqnFormat);
                var isMemberRequired = IsMemberRequired(member);

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
                            MemberKind.Argument, member.Name, memberTypeFqn, isField, isPublic, hasSetter,
                            declaringTypeFqn, name, desc, null,
                            requiredIsSet ? required : null,
                            isMemberRequired,
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
                            MemberKind.Option, member.Name, memberTypeFqn, isField, isPublic, hasSetter,
                            declaringTypeFqn, name, desc, optAliases,
                            requiredIsSet ? required : null,
                            isMemberRequired,
                            order, null, accessPath ?? Array.Empty<AccessPathSegment>()));
                        break;
                    }
                    case "triaxis.CommandLine.OptionsAttribute":
                    {
                        if (memberType is INamedTypeSymbol nestedType)
                        {
                            var segment = new AccessPathSegment(
                                member.Name, memberTypeFqn, isField, isPublic, hasSetter, declaringTypeFqn);
                            var newPath = (accessPath ?? Array.Empty<AccessPathSegment>()).Append(segment).ToArray();
                            CollectMembers(nestedType, members, ct, newPath);
                        }
                        break;
                    }
                    case "triaxis.CommandLine.InjectAttribute":
                    {
                        var injectType = GetNamedArgType(attr, "Type");
                        members.Add(new MemberModel(
                            MemberKind.Inject, member.Name, memberTypeFqn, isField, isPublic, hasSetter,
                            declaringTypeFqn, null, null, null, null, false,
                            0, injectType, accessPath ?? Array.Empty<AccessPathSegment>()));
                        break;
                    }
                }
            }
        }
    }

    private static bool IsMemberRequired(ISymbol member)
    {
        return member.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.RequiredMemberAttribute");
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

    private static string GenerateSource(ImmutableArray<CommandModel> commands, string assemblyName)
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
                    w.WriteLine($"GeneratedCommandRegistration.Register({FormatString(assemblyName)}, AddGeneratedCommands);");
                });
                w.WriteLine();

                // Registration method
                w.Block("private static void AddGeneratedCommands(IToolBuilder builder)", () =>
                {
                    w.WriteLine("var getServiceProvider = builder.GetServiceProviderAccessor();");
                    w.WriteLine();
                    foreach (var cmd in commands)
                    {
                        var safeName = GetSafeName(cmd);
                        w.WriteLine($"{safeName}_Action.Register(builder, getServiceProvider);");
                    }
                });
                w.WriteLine();
            });
            w.WriteLine();

            // Per-command action classes
            foreach (var cmd in commands)
            {
                GenerateCommandAction(w, cmd);
            }
        });

        w.Flush();
        return sw.ToString();
    }

    private static void GenerateCommandAction(IndentedTextWriter w, CommandModel cmd)
    {
        var safeName = GetSafeName(cmd);
        var args = GetArguments(cmd);
        var opts = GetOptions(cmd);
        var injects = cmd.Members.Where(m => m.Kind == MemberKind.Inject).ToArray();
        var nonPublicDirect = args.Concat(opts).Concat(injects).Where(m => !m.IsPublic && m.AccessPath.Length == 0).ToArray();
        var exec = cmd.ExecuteMethod;

        w.Block($"internal sealed class {safeName}_Action : AsynchronousCommandLineAction", () =>
        {
        // Fields
        w.WriteLine("private readonly Func<IServiceProvider> _getServiceProvider;");
        foreach (var arg in args)
        {
            w.WriteLine($"private readonly Argument<{arg.MemberTypeFqn}> _{GetMemberFieldName(arg)};");
        }
        foreach (var opt in opts)
        {
            w.WriteLine($"private readonly Option<{opt.MemberTypeFqn}> _{GetMemberFieldName(opt)};");
        }
        w.WriteLine();

        // Static Register method — creates action, wires up command
        w.Block("public static void Register(IToolBuilder builder, Func<IServiceProvider> getServiceProvider)", () =>
        {
            w.WriteLine($"var cmd = builder.GetCommand({FormatStringArray(cmd.Path)});");
            if (cmd.Description is not null)
            {
                w.WriteLine($"cmd.Description = {FormatString(cmd.Description)};");
            }
            if (cmd.Aliases is { Length: > 0 })
            {
                foreach (var alias in cmd.Aliases)
                {
                    w.WriteLine($"cmd.Aliases.Add({FormatString(alias)});");
                }
            }
            w.WriteLine("var action = new " + safeName + "_Action(getServiceProvider);");
            foreach (var arg in args)
            {
                w.WriteLine($"cmd.Arguments.Add(action._{GetMemberFieldName(arg)});");
            }
            foreach (var opt in opts)
            {
                w.WriteLine($"cmd.Options.Add(action._{GetMemberFieldName(opt)});");
            }
            w.WriteLine("cmd.Action = action;");
        });
        w.WriteLine();

        // Constructor
        w.Block($"private {safeName}_Action(Func<IServiceProvider> getServiceProvider)", () =>
        {
            w.WriteLine("_getServiceProvider = getServiceProvider;");
            foreach (var arg in args)
            {
                var fieldName = GetMemberFieldName(arg);
                w.WriteLine($"_{fieldName} = new Argument<{arg.MemberTypeFqn}>({FormatString(arg.Name ?? arg.MemberName)});");
                if (arg.Description is not null)
                {
                    w.WriteLine($"_{fieldName}.Description = {FormatString(arg.Description)};");
                }
                if (arg.Required == false)
                {
                    w.WriteLine($"_{fieldName}.Arity = new ArgumentArity(0, _{fieldName}.Arity.MaximumNumberOfValues);");
                }
                else if (arg.Required == true || arg.IsMemberRequired)
                {
                    w.WriteLine($"_{fieldName}.Arity = new ArgumentArity(1, _{fieldName}.Arity.MaximumNumberOfValues);");
                }
            }
            foreach (var opt in opts)
            {
                var fieldName = GetMemberFieldName(opt);
                var nameAndAliases = new List<string> { opt.Name ?? opt.MemberName };
                if (opt.Aliases is not null)
                {
                    nameAndAliases.AddRange(opt.Aliases);
                }
                var aliasesArr = nameAndAliases.Skip(1).ToArray();
                if (aliasesArr.Length > 0)
                {
                    w.WriteLine($"_{fieldName} = new Option<{opt.MemberTypeFqn}>({FormatString(nameAndAliases[0])}, {FormatStringArrayInline(aliasesArr)});");
                }
                else
                {
                    w.WriteLine($"_{fieldName} = new Option<{opt.MemberTypeFqn}>({FormatString(nameAndAliases[0])});");
                }
                if (opt.Description is not null)
                {
                    w.WriteLine($"_{fieldName}.Description = {FormatString(opt.Description)};");
                }
                if (opt.Required == true || opt.IsMemberRequired)
                {
                    w.WriteLine($"_{fieldName}.Required = true;");
                }
            }
        });
        w.WriteLine();

        // UnsafeAccessor methods for direct members that need backing field access
        // (non-public members, or public read-only properties)
        var needsAccessor = args.Concat(opts).Concat(injects)
            .Where(m => m.AccessPath.Length == 0 && (!m.IsPublic || !m.HasSetter))
            .ToArray();
        foreach (var member in needsAccessor)
        {
            GenerateUnsafeAccessor(w, cmd.TypeName, member);
        }

        // UnsafeAccessor methods for [Options] path segments
        var pathSegments = new HashSet<string>();
        foreach (var member in args.Concat(opts).Concat(injects))
        {
            foreach (var seg in member.AccessPath)
            {
                if (pathSegments.Add(seg.OwnerTypeFqn + "." + seg.MemberName))
                {
                    if (seg.IsPublic && seg.HasSetter)
                    {
                        // Public settable — no accessor needed, accessed directly
                        continue;
                    }

                    if (seg.IsField)
                    {
                        // Field: ref accessor for read/write
                        w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Field, Name = {FormatString(seg.MemberName)})]");
                        w.WriteLine($"private static extern ref {seg.MemberTypeFqn} {GetPathAccessorName(seg)}({seg.OwnerTypeFqn} instance);");
                    }
                    else if (!seg.HasSetter)
                    {
                        // Read-only property: getter only
                        if (seg.IsPublic)
                        {
                            // Public read-only: accessed directly via property getter, no accessor needed
                            continue;
                        }
                        w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Method, Name = {FormatString("get_" + seg.MemberName)})]");
                        w.WriteLine($"private static extern {seg.MemberTypeFqn} {GetPathAccessorName(seg)}({seg.OwnerTypeFqn} instance);");
                    }
                    else
                    {
                        // Non-public settable property: use backing field ref for read/write
                        w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Field, Name = {FormatString("<" + seg.MemberName + ">k__BackingField")})]");
                        w.WriteLine($"private static extern ref {seg.MemberTypeFqn} {GetPathAccessorName(seg)}({seg.OwnerTypeFqn} instance);");
                    }
                    w.WriteLine();
                }
            }
        }

        // UnsafeAccessor methods for members on nested [Options] types that need backing field access
        foreach (var member in args.Concat(opts).Where(m => m.AccessPath.Length > 0 && (!m.IsPublic || !m.HasSetter)))
        {
            var lastSeg = member.AccessPath[member.AccessPath.Length - 1];
            var fieldName = member.IsField ? member.MemberName : "<" + member.MemberName + ">k__BackingField";
            w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Field, Name = {FormatString(fieldName)})]");
            w.WriteLine($"private static extern ref {member.MemberTypeFqn} {GetAccessorName(member)}({lastSeg.MemberTypeFqn} instance);");
            w.WriteLine();
        }

        // InvokeAsync
        w.Block("public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)", () =>
        {
            w.WriteLine("var provider = _getServiceProvider();");
            w.WriteLine($"var context = new InvocationContext(provider, parseResult, cancellationToken, typeof({cmd.TypeName}));");
            w.WriteLine();
            w.WriteLine("await provider.GetRequiredService<ICommandExecutor>().ExecuteAsync(context, async () =>");
            w.Block(() =>
            {
                w.WriteLine($"var instance = ActivatorUtilities.CreateInstance<{cmd.TypeName}>(provider);");
                w.WriteLine();

                // Direct [Inject] assignments
                foreach (var inject in injects)
                {
                    var serviceType = inject.InjectTypeFqn ?? inject.MemberTypeFqn;
                    var assignment = inject.IsPublic
                        ? $"instance.{inject.MemberName}"
                        : $"{GetAccessorName(inject)}(instance)";
                    w.WriteLine($"{assignment} = provider.GetRequiredService<{serviceType}>();");
                }

                // Eagerly resolve [Options] nested objects
                GenerateOptionsPathResolution(w, cmd, args, opts);

                // Inline bind: arguments
                foreach (var arg in args)
                {
                    var fieldName = GetMemberFieldName(arg);
                    w.Block($"if (parseResult.GetResult(_{fieldName}) is {{ }} {fieldName}_result && {fieldName}_result.Tokens.Any())", () =>
                    {
                        WriteMemberAssignment(w, arg, $"parseResult.GetValue(_{fieldName})");
                    });
                }

                // Inline bind: options
                foreach (var opt in opts)
                {
                    var fieldName = GetMemberFieldName(opt);
                    w.Block($"if (parseResult.GetResult(_{fieldName}) is {{ }} {fieldName}_result && !{fieldName}_result.Implicit)", () =>
                    {
                        WriteMemberAssignment(w, opt, $"parseResult.GetValue(_{fieldName})");
                    });
                }

                if (args.Length > 0 || opts.Length > 0)
                {
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

    private static void GenerateUnsafeAccessor(IndentedTextWriter w, string typeName, MemberModel member)
    {
        var fieldName = member.IsField ? member.MemberName : "<" + member.MemberName + ">k__BackingField";
        w.WriteLine($"[UnsafeAccessor(UnsafeAccessorKind.Field, Name = {FormatString(fieldName)})]");
        w.WriteLine($"private static extern ref {member.MemberTypeFqn} {GetAccessorName(member)}({typeName} instance);");
        w.WriteLine();
    }

    private static string GetAccessorName(MemberModel member)
    {
        return $"__access_{GetMemberFieldName(member)}";
    }

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

    private static void WriteMemberAssignment(IndentedTextWriter w, MemberModel member, string valueExpr)
    {
        // For members with an access path, the path variable is resolved eagerly
        // in GenerateOptionsPathResolution and named __opts_{segment}
        var target = "instance";
        if (member.AccessPath.Length > 0)
        {
            target = "__opts_" + string.Join("_", member.AccessPath.Select(s => s.MemberName));
        }

        if (member.IsPublic && member.HasSetter)
        {
            w.WriteLine($"{target}.{member.MemberName} = {valueExpr};");
        }
        else
        {
            // Use UnsafeAccessor for non-public members, or public read-only properties (backing field)
            w.WriteLine($"{GetAccessorName(member)}({target}) = {valueExpr};");
        }
    }

    /// <summary>
    /// Generates eager resolution of all [Options] path prefixes used by arguments/options.
    /// Called once after instance creation, before any binding.
    /// </summary>
    private static void GenerateOptionsPathResolution(IndentedTextWriter w, CommandModel cmd, MemberModel[] args, MemberModel[] opts)
    {
        // Collect all unique path prefixes
        var resolved = new HashSet<string>();
        foreach (var member in args.Concat(opts))
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

                var createExpr = $"Activator.CreateInstance<{seg.MemberTypeFqn}>()";
                if (seg.IsField || seg.HasSetter)
                {
                    // Field or settable property: read, create if null, assign back
                    if (seg.IsPublic && seg.HasSetter)
                    {
                        w.WriteLine($"var {varName} = {parentVar}.{seg.MemberName} ?? ({parentVar}.{seg.MemberName} = {createExpr});");
                    }
                    else
                    {
                        w.WriteLine($"ref var {varName}_slot = ref {GetPathAccessorName(seg)}({parentVar});");
                        w.WriteLine($"var {varName} = {varName}_slot ?? ({varName}_slot = {createExpr});");
                    }
                }
                else
                {
                    // Read-only property: just read, expect pre-initialized
                    if (seg.IsPublic)
                    {
                        w.WriteLine($"var {varName} = {parentVar}.{seg.MemberName} ?? throw new InvalidOperationException(\"[Options] property '{seg.MemberName}' returned null but has no setter\");");
                    }
                    else
                    {
                        w.WriteLine($"var {varName} = {GetPathAccessorName(seg)}({parentVar}) ?? throw new InvalidOperationException(\"[Options] property '{seg.MemberName}' returned null but has no setter\");");
                    }
                }
            }
        }
    }

    private static string GetPathAccessorName(AccessPathSegment seg)
    {
        return $"__access_path_{seg.MemberName}";
    }

    private static MemberModel[] GetArguments(CommandModel cmd) =>
        cmd.Members.Where(m => m.Kind == MemberKind.Argument).OrderBy(m => m.Order).ThenBy(m => m.Name).ToArray();

    private static MemberModel[] GetOptions(CommandModel cmd) =>
        cmd.Members.Where(m => m.Kind == MemberKind.Option).OrderBy(m => m.Order).ThenBy(m => m.Name).ToArray();

    private static string GetSafeName(CommandModel cmd)
    {
        return string.Join("_", cmd.Path).Replace("-", "_");
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
    ImmutableArray<MemberModel> Members,
    ExecuteMethodModel ExecuteMethod);

record AccessPathSegment(
    string MemberName,
    string MemberTypeFqn,
    bool IsField,
    bool IsPublic,
    bool HasSetter,
    string OwnerTypeFqn);

record MemberModel(
    MemberKind Kind,
    string MemberName,
    string MemberTypeFqn,
    bool IsField,
    bool IsPublic,
    bool HasSetter,
    string DeclaringTypeFqn,
    string? Name,
    string? Description,
    string[]? Aliases,
    bool? Required,
    bool IsMemberRequired,
    double Order,
    string? InjectTypeFqn,
    AccessPathSegment[] AccessPath);

static class IndentedTextWriterExtensions
{
    public static void Block(this IndentedTextWriter w, Action body)
    {
        w.WriteLine("{");
        w.Indent++;
        body();
        w.Indent--;
        w.WriteLine("}");
    }

    public static void Block(this IndentedTextWriter w, string header, Action body)
    {
        w.WriteLine(header);
        w.Block(body);
    }
}
