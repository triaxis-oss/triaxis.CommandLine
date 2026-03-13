using System.CodeDom.Compiler;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace triaxis.CommandLine.SourceGenerator;

[Generator]
public class CommandTreeGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all classes decorated with [Command]
        var commandClasses = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "triaxis.CommandLine.CommandAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractCommandModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        // Collect all command models and generate the registration code
        var collected = commandClasses.Collect();

        context.RegisterSourceOutput(collected, static (spc, commands) =>
        {
            if (commands.IsDefaultOrEmpty)
                return;

            var source = GenerateSource(commands);
            spc.AddSource("GeneratedCommandTree.g.cs", source);
        });
    }

    private static CommandModel? ExtractCommandModel(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var typeSymbol = (INamedTypeSymbol)ctx.TargetSymbol;

        // Find the CommandAttribute
        var commandAttr = ctx.Attributes
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "triaxis.CommandLine.CommandAttribute");
        if (commandAttr is null)
            return null;

        // Extract path from constructor args
        var path = commandAttr.ConstructorArguments
            .SelectMany(a => a.Kind == TypedConstantKind.Array
                ? a.Values.Select(v => v.Value?.ToString() ?? "")
                : new[] { a.Value?.ToString() ?? "" })
            .ToArray();

        // Extract named args
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
                    aliases = named.Value.Values.Select(v => v.Value?.ToString() ?? "").ToArray();
                    break;
            }
        }

        // Find members with [Argument], [Option], [Options], [Inject]
        var members = new List<MemberModel>();
        CollectMembers(typeSymbol, members, ImmutableArray<string>.Empty);

        // Find Execute/ExecuteAsync method
        var executeInfo = FindExecuteMethod(typeSymbol);

        return new CommandModel(
            TypeName: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TypeMetadataName: typeSymbol.ToDisplayString(),
            Path: path,
            Description: description,
            Aliases: aliases,
            Members: members.ToArray(),
            Execute: executeInfo);
    }

    private static void CollectMembers(INamedTypeSymbol type, List<MemberModel> members, ImmutableArray<string> accessPath)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol and not IPropertySymbol)
                continue;

            foreach (var attr in member.GetAttributes())
            {
                var attrName = attr.AttributeClass?.ToDisplayString();

                switch (attrName)
                {
                    case "triaxis.CommandLine.ArgumentAttribute":
                        members.Add(ExtractArgumentMember(member, attr, accessPath));
                        break;
                    case "triaxis.CommandLine.OptionAttribute":
                        members.Add(ExtractOptionMember(member, attr, accessPath));
                        break;
                    case "triaxis.CommandLine.OptionsAttribute":
                        var nestedType = GetMemberType(member);
                        if (nestedType is INamedTypeSymbol namedNested)
                        {
                            var newPath = accessPath.Add(member.Name);
                            CollectMembers(namedNested, members, newPath);
                        }
                        break;
                    case "triaxis.CommandLine.InjectAttribute":
                        members.Add(ExtractInjectMember(member, attr, accessPath));
                        break;
                }
            }
        }
    }

    private static MemberModel ExtractArgumentMember(ISymbol member, AttributeData attr, ImmutableArray<string> accessPath)
    {
        string? name = null;
        string? description = null;
        bool? required = null;
        double order = 0;

        // Constructor args
        if (attr.ConstructorArguments.Length >= 1 && attr.ConstructorArguments[0].Value is string n)
            name = n;
        if (attr.ConstructorArguments.Length >= 2 && attr.ConstructorArguments[1].Value is string d)
            description = d;

        // Named args
        foreach (var named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Name": name = named.Value.Value?.ToString(); break;
                case "Description": description = named.Value.Value?.ToString(); break;
                case "Required": required = (bool?)named.Value.Value; break;
                case "Order": order = Convert.ToDouble(named.Value.Value); break;
            }
        }

        var memberType = GetMemberType(member);
        var actualType = UnwrapNullable(memberType);

        return new MemberModel(
            Kind: MemberKind.Argument,
            MemberName: member.Name,
            MemberTypeFqn: actualType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsNullable: memberType.NullableAnnotation == NullableAnnotation.Annotated ||
                        (memberType is INamedTypeSymbol nts && nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T),
            IsField: member is IFieldSymbol,
            IsPublic: member.DeclaredAccessibility == Accessibility.Public,
            DeclaringTypeFqn: member.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Name: name ?? member.Name,
            Description: description,
            Aliases: null,
            Required: required,
            Order: order,
            InjectTypeFqn: null,
            AccessPath: accessPath.ToArray());
    }

    private static MemberModel ExtractOptionMember(ISymbol member, AttributeData attr, ImmutableArray<string> accessPath)
    {
        string? name = null;
        string? description = null;
        string[]? aliases = null;
        bool required = false;
        double order = 0;

        // Constructor args
        if (attr.ConstructorArguments.Length >= 1 && attr.ConstructorArguments[0].Value is string n)
            name = n;
        if (attr.ConstructorArguments.Length >= 2 && attr.ConstructorArguments[1].Kind == TypedConstantKind.Array)
            aliases = attr.ConstructorArguments[1].Values.Select(v => v.Value?.ToString() ?? "").ToArray();

        // Named args
        foreach (var named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Name": name = named.Value.Value?.ToString(); break;
                case "Description": description = named.Value.Value?.ToString(); break;
                case "Aliases": aliases = named.Value.Values.Select(v => v.Value?.ToString() ?? "").ToArray(); break;
                case "Required": required = (bool)(named.Value.Value ?? false); break;
                case "Order": order = Convert.ToDouble(named.Value.Value); break;
            }
        }

        var memberType = GetMemberType(member);
        var actualType = UnwrapNullable(memberType);

        return new MemberModel(
            Kind: MemberKind.Option,
            MemberName: member.Name,
            MemberTypeFqn: actualType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsNullable: memberType.NullableAnnotation == NullableAnnotation.Annotated ||
                        (memberType is INamedTypeSymbol nts && nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T),
            IsField: member is IFieldSymbol,
            IsPublic: member.DeclaredAccessibility == Accessibility.Public,
            DeclaringTypeFqn: member.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Name: name ?? member.Name,
            Description: description,
            Aliases: aliases,
            Required: required,
            Order: order,
            InjectTypeFqn: null,
            AccessPath: accessPath.ToArray());
    }

    private static MemberModel ExtractInjectMember(ISymbol member, AttributeData attr, ImmutableArray<string> accessPath)
    {
        string? injectType = null;

        // Constructor arg: Type
        if (attr.ConstructorArguments.Length >= 1 && attr.ConstructorArguments[0].Value is INamedTypeSymbol typeArg)
            injectType = typeArg.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Named arg
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "Type" && named.Value.Value is INamedTypeSymbol namedType)
                injectType = namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        var memberType = GetMemberType(member);

        // Special case: ILogger -> ILogger<CommandType>
        var resolveType = injectType ?? memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new MemberModel(
            Kind: MemberKind.Inject,
            MemberName: member.Name,
            MemberTypeFqn: memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsNullable: false,
            IsField: member is IFieldSymbol,
            IsPublic: member.DeclaredAccessibility == Accessibility.Public,
            DeclaringTypeFqn: member.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Name: null,
            Description: null,
            Aliases: null,
            Required: null,
            Order: 0,
            InjectTypeFqn: resolveType,
            AccessPath: accessPath.ToArray());
    }

    private static ExecuteMethodInfo? FindExecuteMethod(INamedTypeSymbol type)
    {
        // Look for ExecuteAsync(CancellationToken), ExecuteAsync(), Execute()
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method)
                continue;

            if (method.Name == "ExecuteAsync" && method.Parameters.Length == 1 &&
                method.Parameters[0].Type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                return new ExecuteMethodInfo(
                    MethodName: "ExecuteAsync",
                    TakesCancellationToken: true,
                    ReturnTypeFqn: method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    IsAsync: IsAsyncReturnType(method.ReturnType));
            }

            if (method.Name == "ExecuteAsync" && method.Parameters.Length == 0)
            {
                return new ExecuteMethodInfo(
                    MethodName: "ExecuteAsync",
                    TakesCancellationToken: false,
                    ReturnTypeFqn: method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    IsAsync: IsAsyncReturnType(method.ReturnType));
            }

            if (method.Name == "Execute" && method.Parameters.Length == 0)
            {
                return new ExecuteMethodInfo(
                    MethodName: "Execute",
                    TakesCancellationToken: false,
                    ReturnTypeFqn: method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    IsAsync: IsAsyncReturnType(method.ReturnType));
            }
        }

        return null;
    }

    private static bool IsAsyncReturnType(ITypeSymbol returnType)
    {
        var name = returnType.ToDisplayString();
        return name.StartsWith("System.Threading.Tasks.Task") ||
               name.StartsWith("System.Threading.Tasks.ValueTask") ||
               name.Contains("IAsyncEnumerable");
    }

    private static ITypeSymbol GetMemberType(ISymbol member)
    {
        return member switch
        {
            IFieldSymbol f => f.Type,
            IPropertySymbol p => p.Type,
            _ => throw new InvalidOperationException()
        };
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol nts &&
            nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            nts.TypeArguments.Length == 1)
        {
            return nts.TypeArguments[0];
        }
        return type;
    }

    private static string GenerateSource(ImmutableArray<CommandModel> commands)
    {
        var sw = new StringWriter();
        var w = new IndentedTextWriter(sw);

        w.WriteLine("// <auto-generated/>");
        w.WriteLine("#nullable enable");
        w.WriteLine();
        w.WriteLine("using System;");
        w.WriteLine("using System.CommandLine;");
        w.WriteLine("using System.CommandLine.Hosting;");
        w.WriteLine("using System.CommandLine.Invocation;");
        w.WriteLine("using System.CommandLine.Parsing;");
        w.WriteLine("using System.Reflection;");
        w.WriteLine("using System.Threading;");
        w.WriteLine("using System.Threading.Tasks;");
        w.WriteLine("using Microsoft.Extensions.DependencyInjection;");
        w.WriteLine("using Microsoft.Extensions.Hosting;");
        w.WriteLine();
        w.WriteLine("namespace triaxis.CommandLine.Generated");
        w.WriteLine("{");
        w.Indent++;

        // Extension method
        w.WriteLine("internal static class GeneratedToolBuilderExtensions");
        w.WriteLine("{");
        w.Indent++;

        w.WriteLine("public static IToolBuilder AddGeneratedCommands(this IToolBuilder builder)");
        w.WriteLine("{");
        w.Indent++;
        foreach (var cmd in commands)
        {
            var safeName = GetSafeName(cmd);
            w.WriteLine($"{safeName}_Register(builder);");
        }
        w.WriteLine();
        w.WriteLine("builder.ConfigureServices((_, services) =>");
        w.WriteLine("{");
        w.Indent++;
        foreach (var cmd in commands)
        {
            w.WriteLine($"services.AddTransient<{cmd.TypeName}>();");
        }
        w.Indent--;
        w.WriteLine("});");
        w.WriteLine();
        w.WriteLine("return builder;");
        w.Indent--;
        w.WriteLine("}");
        w.WriteLine();

        // Per-command registration methods
        foreach (var cmd in commands)
        {
            GenerateCommandRegistration(w, cmd);
        }

        w.Indent--;
        w.WriteLine("}");
        w.WriteLine();

        // Per-command handler classes
        foreach (var cmd in commands)
        {
            GenerateCommandHandler(w, cmd);
        }

        w.Indent--;
        w.WriteLine("}");

        w.Flush();
        return sw.ToString();
    }

    private static void GenerateCommandRegistration(IndentedTextWriter w, CommandModel cmd)
    {
        var safeName = GetSafeName(cmd);
        var args = cmd.Members.Where(m => m.Kind == MemberKind.Argument).OrderBy(m => m.Order).ThenBy(m => m.Name).ToArray();
        var opts = cmd.Members.Where(m => m.Kind == MemberKind.Option).OrderBy(m => m.Order).ThenBy(m => m.Name).ToArray();

        w.WriteLine($"private static void {safeName}_Register(IToolBuilder builder)");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine($"var cmd = builder.GetCommand({FormatStringArray(cmd.Path)});");

        if (cmd.Description is not null)
            w.WriteLine($"cmd.Description = {FormatString(cmd.Description)};");

        if (cmd.Aliases is { Length: > 0 })
        {
            foreach (var alias in cmd.Aliases)
                w.WriteLine($"cmd.AddAlias({FormatString(alias)});");
        }

        w.WriteLine($"var handler = new {safeName}_Handler();");

        foreach (var arg in args)
        {
            w.WriteLine($"cmd.AddArgument(handler._{GetMemberFieldName(arg)});");
        }

        foreach (var opt in opts)
        {
            w.WriteLine($"cmd.AddOption(handler._{GetMemberFieldName(opt)});");
        }

        w.WriteLine("cmd.Handler = handler;");
        w.Indent--;
        w.WriteLine("}");
        w.WriteLine();
    }

    private static void GenerateCommandHandler(IndentedTextWriter w, CommandModel cmd)
    {
        var safeName = GetSafeName(cmd);
        var args = cmd.Members.Where(m => m.Kind == MemberKind.Argument).OrderBy(m => m.Order).ThenBy(m => m.Name).ToArray();
        var opts = cmd.Members.Where(m => m.Kind == MemberKind.Option).OrderBy(m => m.Order).ThenBy(m => m.Name).ToArray();
        var injects = cmd.Members.Where(m => m.Kind == MemberKind.Inject).ToArray();

        w.WriteLine($"internal sealed class {safeName}_Handler : ICommandHandler");
        w.WriteLine("{");
        w.Indent++;

        // Argument/Option fields
        foreach (var arg in args)
        {
            var fieldName = GetMemberFieldName(arg);
            w.WriteLine($"internal readonly Argument<{arg.MemberTypeFqn}> _{fieldName};");
        }
        foreach (var opt in opts)
        {
            var fieldName = GetMemberFieldName(opt);
            w.WriteLine($"internal readonly Option<{opt.MemberTypeFqn}> _{fieldName};");
        }

        w.WriteLine();

        // Cached reflection for setting private fields
        var allBindable = args.Concat(opts).Concat(injects).ToArray();
        foreach (var m in allBindable)
        {
            var fieldName = GetMemberFieldName(m);
            var memberKind = m.IsField ? "Field" : "Property";
            var flags = m.IsPublic ? "BindingFlags.Public | BindingFlags.Instance" : "BindingFlags.NonPublic | BindingFlags.Instance";
            w.WriteLine($"private static readonly {memberKind}Info s_{fieldName} = typeof({m.DeclaringTypeFqn}).Get{memberKind}({FormatString(m.MemberName)}, {flags})!;");
        }

        w.WriteLine();

        // Constructor - initialize arguments and options
        w.WriteLine($"public {safeName}_Handler()");
        w.WriteLine("{");
        w.Indent++;
        foreach (var arg in args)
        {
            var fieldName = GetMemberFieldName(arg);
            w.WriteLine($"_{fieldName} = new Argument<{arg.MemberTypeFqn}>({FormatString(arg.Name)});");
            if (arg.Description is not null)
                w.WriteLine($"_{fieldName}.Description = {FormatString(arg.Description)};");
            if (arg.Required == false)
                w.WriteLine($"_{fieldName}.Arity = new ArgumentArity(0, _{fieldName}.Arity.MaximumNumberOfValues);");
            else if (arg.Required == true)
                w.WriteLine($"_{fieldName}.Arity = new ArgumentArity(1, _{fieldName}.Arity.MaximumNumberOfValues);");
        }
        foreach (var opt in opts)
        {
            var fieldName = GetMemberFieldName(opt);
            var nameAndAliases = new List<string> { opt.Name };
            if (opt.Aliases is not null)
                nameAndAliases.AddRange(opt.Aliases);
            w.WriteLine($"_{fieldName} = new Option<{opt.MemberTypeFqn}>({FormatStringArrayInline(nameAndAliases.ToArray())});");
            if (opt.Description is not null)
                w.WriteLine($"_{fieldName}.Description = {FormatString(opt.Description)};");
            if (opt.Required == true)
                w.WriteLine($"_{fieldName}.IsRequired = true;");
        }
        w.Indent--;
        w.WriteLine("}");
        w.WriteLine();

        // Invoke
        w.WriteLine("public int Invoke(InvocationContext context) => InvokeAsync(context).GetAwaiter().GetResult();");
        w.WriteLine();

        // InvokeAsync
        w.WriteLine("public async Task<int> InvokeAsync(InvocationContext context)");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine("var host = context.GetHost();");
        w.WriteLine("var sp = host.Services;");
        w.WriteLine($"var instance = sp.GetRequiredService<{cmd.TypeName}>();");
        w.WriteLine();

        // Inject members
        foreach (var inject in injects)
        {
            var fieldName = GetMemberFieldName(inject);
            var resolveType = inject.InjectTypeFqn!;

            // Special case: ILogger -> ILogger<CommandType>
            if (resolveType == "Microsoft.Extensions.Logging.ILogger")
                resolveType = $"Microsoft.Extensions.Logging.ILogger<{cmd.TypeName}>";

            w.WriteLine($"s_{fieldName}.SetValue(instance, sp.GetRequiredService<{resolveType}>());");
        }

        if (injects.Any())
            w.WriteLine();

        // Bind arguments
        w.WriteLine("var parseResult = context.ParseResult;");
        foreach (var arg in args)
        {
            var fieldName = GetMemberFieldName(arg);
            w.WriteLine($"if (parseResult.FindResultFor(_{fieldName}) is {{ }} res_{fieldName} && res_{fieldName}.Tokens.Count > 0)");
            w.WriteLine("{");
            w.Indent++;
            w.WriteLine($"s_{fieldName}.SetValue(instance, res_{fieldName}.GetValueOrDefault<{arg.MemberTypeFqn}>());");
            w.Indent--;
            w.WriteLine("}");
        }

        // Bind options
        foreach (var opt in opts)
        {
            var fieldName = GetMemberFieldName(opt);
            w.WriteLine($"if (parseResult.FindResultFor(_{fieldName}) is {{ }} res_{fieldName} && !res_{fieldName}.IsImplicit)");
            w.WriteLine("{");
            w.Indent++;
            w.WriteLine($"s_{fieldName}.SetValue(instance, res_{fieldName}.GetValueOrDefault<{opt.MemberTypeFqn}>());");
            w.Indent--;
            w.WriteLine("}");
        }

        w.WriteLine();

        // Execute
        if (cmd.Execute is not null)
        {
            var exec = cmd.Execute;
            var cancellationToken = "context.GetCancellationToken()";

            if (!exec.TakesCancellationToken)
            {
                w.WriteLine($"{cancellationToken}.Register(() => Environment.FailFast(null));");
                w.WriteLine();
            }

            string callExpr;
            if (exec.TakesCancellationToken)
                callExpr = $"instance.{exec.MethodName}({cancellationToken})";
            else
                callExpr = $"instance.{exec.MethodName}()";

            if (exec.ReturnTypeFqn == "System.Threading.Tasks.Task" || exec.ReturnTypeFqn == "global::System.Threading.Tasks.Task")
            {
                w.WriteLine($"await {callExpr};");
                w.WriteLine("return 0;");
            }
            else if (exec.ReturnTypeFqn == "void")
            {
                w.WriteLine($"{callExpr};");
                w.WriteLine("return 0;");
            }
            else
            {
                w.WriteLine($"var result = {callExpr};");
                w.WriteLine($"context.InvocationResult = triaxis.CommandLine.Invocation.CommandInvocationResult.Create(result, typeof({exec.ReturnTypeFqn}));");
                w.WriteLine("return 0;");
            }
        }
        else
        {
            w.WriteLine("throw new InvalidOperationException(\"No Execute/ExecuteAsync method found\");");
        }

        w.Indent--;
        w.WriteLine("}");
        w.Indent--;
        w.WriteLine("}");
        w.WriteLine();
    }

    private static string GetSafeName(CommandModel cmd)
    {
        return cmd.TypeMetadataName.Replace('.', '_').Replace('+', '_').Replace('<', '_').Replace('>', '_');
    }

    private static string GetMemberFieldName(MemberModel member)
    {
        var prefix = member.AccessPath.Length > 0
            ? string.Join("_", member.AccessPath) + "_"
            : "";
        return prefix + member.MemberName.TrimStart('_');
    }

    private static string FormatString(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string FormatStringArray(string[] values)
    {
        return string.Join(", ", values.Select(FormatString));
    }

    private static string FormatStringArrayInline(string[] values)
    {
        if (values.Length == 1)
            return FormatString(values[0]);
        return "new[] { " + string.Join(", ", values.Select(FormatString)) + " }";
    }
}

// Model types

internal record CommandModel(
    string TypeName,
    string TypeMetadataName,
    string[] Path,
    string? Description,
    string[]? Aliases,
    MemberModel[] Members,
    ExecuteMethodInfo? Execute);

internal record MemberModel(
    MemberKind Kind,
    string MemberName,
    string MemberTypeFqn,
    bool IsNullable,
    bool IsField,
    bool IsPublic,
    string DeclaringTypeFqn,
    string? Name,
    string? Description,
    string[]? Aliases,
    bool? Required,
    double Order,
    string? InjectTypeFqn,
    string[] AccessPath);

internal record ExecuteMethodInfo(
    string MethodName,
    bool TakesCancellationToken,
    string ReturnTypeFqn,
    bool IsAsync);

internal enum MemberKind
{
    Argument,
    Option,
    Inject,
}
