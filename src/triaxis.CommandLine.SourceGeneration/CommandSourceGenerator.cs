using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace triaxis.CommandLine.SourceGeneration;

/// <summary>
/// Roslyn incremental source generator that replaces the runtime reflection-based
/// command discovery (<c>AddCommandsFromAssembly</c>) with compile-time code generation.
///
/// For each type decorated with <c>[Command(...)]</c> the generator emits an
/// <c>AddGeneratedCommands(IToolBuilder)</c> extension method that:
/// <list type="bullet">
///   <item>Creates the command tree node with description/aliases.</item>
///   <item>Adds <c>Argument&lt;T&gt;</c> and <c>Option&lt;T&gt;</c> symbols.</item>
///   <item>Registers a <see cref="GeneratedCommandHandler"/> with a compiled binder
///         delegate so that argument/option values are set without assembly scanning.</item>
///   <item>Registers the command type in the DI container via
///         <c>services.AddTransient&lt;T&gt;()</c>.</item>
/// </list>
/// </summary>
[Generator]
public sealed class CommandSourceGenerator : IIncrementalGenerator
{
    // Attribute FQNs we look for
    private const string CommandAttributeFqn = "triaxis.CommandLine.CommandAttribute";
    private const string ArgumentAttributeFqn = "triaxis.CommandLine.ArgumentAttribute";
    private const string OptionAttributeFqn = "triaxis.CommandLine.OptionAttribute";
    private const string OptionsAttributeFqn = "triaxis.CommandLine.OptionsAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Collect all types that carry [Command] attributes
        var commandTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                CommandAttributeFqn,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => GetCommandTypeInfo(ctx))
            .Where(static t => t is not null)
            .Select(static (t, _) => t!);

        // Collect all [assembly: Command(...)] usages (path-only, no handler)
        var assemblyCommands = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                CommandAttributeFqn,
                predicate: static (node, _) => node is CompilationUnitSyntax,
                transform: static (ctx, _) => GetAssemblyCommandAttributes(ctx))
            .SelectMany(static (list, _) => list);

        var allCommandTypes = commandTypes.Collect();
        var allAssemblyCommands = assemblyCommands.Collect();

        context.RegisterSourceOutput(
            allCommandTypes.Combine(allAssemblyCommands),
            static (spc, combined) => GenerateSource(spc, combined.Left, combined.Right));
    }

    // ─── Data models ────────────────────────────────────────────────────────────

    private sealed class CommandTypeInfo
    {
        public string FullName { get; set; } = "";
        public string Namespace { get; set; } = "";
        public IReadOnlyList<CommandAttrData> Attributes { get; set; } = Array.Empty<CommandAttrData>();
        public IReadOnlyList<MemberBindingData> Members { get; set; } = Array.Empty<MemberBindingData>();
    }

    private sealed class CommandAttrData
    {
        public string[] Path { get; set; } = Array.Empty<string>();
        public string? Description { get; set; }
        public string[]? Aliases { get; set; }
    }

    private sealed class MemberBindingData
    {
        public string MemberName { get; set; } = "";
        public string TypeFullName { get; set; } = "";
        public bool IsField { get; set; }
        public bool IsPublic { get; set; }
        public bool IsReadOnly { get; set; }
        public MemberBindingKind Kind { get; set; }
        // For Argument / Option
        public string? CommandLineName { get; set; }
        public string? Description { get; set; }
        public string[]? Aliases { get; set; }
        public bool? Required { get; set; }
        public double Order { get; set; }
        // For Options (nested)
        public IReadOnlyList<MemberBindingData> NestedMembers { get; set; } = Array.Empty<MemberBindingData>();
        public string NestedTypeFqn { get; set; } = "";
    }

    private enum MemberBindingKind { Argument, Option, Options }

    // ─── Extraction helpers ──────────────────────────────────────────────────────

    private static CommandTypeInfo? GetCommandTypeInfo(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
            return null;

        var attrs = new List<CommandAttrData>();
        foreach (var attr in typeSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == CommandAttributeFqn)
            {
                attrs.Add(ExtractCommandAttrData(attr));
            }
        }

        if (attrs.Count == 0)
            return null;

        var members = ExtractMemberBindings(typeSymbol);

        return new CommandTypeInfo
        {
            FullName = typeSymbol.ToDisplayString(),
            Namespace = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            Attributes = attrs,
            Members = members,
        };
    }

    private static IReadOnlyList<string> GetAssemblyCommandAttributes(GeneratorAttributeSyntaxContext ctx)
    {
        // Assembly-level [Command] attributes are handled by the assembly-command pipeline;
        // we just collect path info here for creating empty command nodes.
        var result = new List<string>();
        foreach (var attr in ctx.Attributes)
        {
            if (attr.AttributeClass?.ToDisplayString() != CommandAttributeFqn)
                continue;

            var path = ExtractCommandAttrData(attr).Path;
            if (path.Length > 0)
                result.Add(string.Join("/", path));
        }
        return result;
    }

    private static CommandAttrData ExtractCommandAttrData(AttributeData attr)
    {
        // Constructor args: params string[] path
        var path = new List<string>();
        foreach (var arg in attr.ConstructorArguments)
        {
            if (arg.Kind == TypedConstantKind.Array)
            {
                foreach (var v in arg.Values)
                    if (v.Value is string s) path.Add(s);
            }
            else if (arg.Value is string s)
            {
                path.Add(s);
            }
        }

        string? description = null;
        string[]? aliases = null;

        foreach (var named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Description" when named.Value.Value is string s:
                    description = s;
                    break;
                case "Aliases" when named.Value.Kind == TypedConstantKind.Array:
                    aliases = named.Value.Values.Select(v => v.Value?.ToString() ?? "").ToArray();
                    break;
            }
        }

        return new CommandAttrData { Path = path.ToArray(), Description = description, Aliases = aliases };
    }

    private static IReadOnlyList<MemberBindingData> ExtractMemberBindings(
        INamedTypeSymbol typeSymbol, bool includeInherited = true)
    {
        var result = new List<MemberBindingData>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Walk type hierarchy so inherited members are included
        for (var t = typeSymbol; t != null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers())
            {
                if (member.IsStatic) continue;
                if (member is not (IFieldSymbol or IPropertySymbol)) continue;

                if (!seen.Add(member.Name)) continue;

                foreach (var attr in member.GetAttributes())
                {
                    var attrFqn = attr.AttributeClass?.ToDisplayString();
                    if (attrFqn == ArgumentAttributeFqn)
                    {
                        result.Add(ExtractArgumentData(member, attr));
                        break;
                    }
                    if (attrFqn == OptionAttributeFqn)
                    {
                        result.Add(ExtractOptionData(member, attr));
                        break;
                    }
                    if (attrFqn == OptionsAttributeFqn)
                    {
                        var memberType = GetMemberType(member);
                        if (memberType is INamedTypeSymbol nestedType)
                        {
                            result.Add(new MemberBindingData
                            {
                                MemberName = member.Name,
                                TypeFullName = GetTypeRef(nestedType),
                                IsField = member is IFieldSymbol,
                                IsPublic = member.DeclaredAccessibility == Accessibility.Public,
                                Kind = MemberBindingKind.Options,
                                NestedMembers = ExtractMemberBindings(nestedType, false),
                                NestedTypeFqn = GetTypeRef(nestedType),
                            });
                        }
                        break;
                    }
                }
            }
        }

        // Sort by Order, then Name
        result.Sort((a, b) =>
        {
            var cmp = a.Order.CompareTo(b.Order);
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.CommandLineName, b.CommandLineName, StringComparison.Ordinal);
            if (cmp != 0) return cmp;
            return string.Compare(a.MemberName, b.MemberName, StringComparison.Ordinal);
        });

        return result;
    }

    private static MemberBindingData ExtractArgumentData(ISymbol member, AttributeData attr)
    {
        string? name = null;
        string? description = null;
        bool? required = null;
        double order = 0;

        // ctor: (string? name = null, string? description = null)
        var ctorArgs = attr.ConstructorArguments;
        if (ctorArgs.Length >= 1 && ctorArgs[0].Value is string n) name = n;
        if (ctorArgs.Length >= 2 && ctorArgs[1].Value is string d) description = d;

        foreach (var named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Name" when named.Value.Value is string s: name = s; break;
                case "Description" when named.Value.Value is string s: description = s; break;
                case "Required" when named.Value.Value is bool b: required = b; break;
                case "Order" when named.Value.Value is double dbl: order = dbl; break;
            }
        }

        var memberType = GetMemberType(member);
        return new MemberBindingData
        {
            MemberName = member.Name,
            TypeFullName = memberType != null ? GetTypeRef(memberType) : "object",
            IsField = member is IFieldSymbol,
            IsPublic = member.DeclaredAccessibility == Accessibility.Public,
            IsReadOnly = member is IFieldSymbol fi && fi.IsReadOnly
                      || member is IPropertySymbol pi && pi.IsReadOnly,
            Kind = MemberBindingKind.Argument,
            CommandLineName = name,
            Description = description,
            Required = required,
            Order = order,
        };
    }

    private static MemberBindingData ExtractOptionData(ISymbol member, AttributeData attr)
    {
        string? name = null;
        string? description = null;
        string[]? aliases = null;
        bool? required = null;
        double order = 0;

        // ctor overloads:
        //   ()
        //   (string? name)
        //   (string? name, params string[] aliases)
        var ctorArgs = attr.ConstructorArguments;
        if (ctorArgs.Length >= 1 && ctorArgs[0].Value is string n) name = n;
        if (ctorArgs.Length >= 2)
        {
            var second = ctorArgs[1];
            if (second.Kind == TypedConstantKind.Array)
                aliases = second.Values.Select(v => v.Value?.ToString() ?? "").ToArray();
            else if (second.Value is string s)
                aliases = new[] { s };
        }

        foreach (var named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Name" when named.Value.Value is string s: name = s; break;
                case "Description" when named.Value.Value is string s: description = s; break;
                case "Required" when named.Value.Value is bool b: required = b; break;
                case "Order" when named.Value.Value is double dbl: order = dbl; break;
                case "Aliases" when named.Value.Kind == TypedConstantKind.Array:
                    aliases = named.Value.Values.Select(v => v.Value?.ToString() ?? "").ToArray();
                    break;
            }
        }

        var memberType = GetMemberType(member);
        return new MemberBindingData
        {
            MemberName = member.Name,
            TypeFullName = memberType != null ? GetTypeRef(memberType) : "object",
            IsField = member is IFieldSymbol,
            IsPublic = member.DeclaredAccessibility == Accessibility.Public,
            IsReadOnly = member is IFieldSymbol fi && fi.IsReadOnly
                      || member is IPropertySymbol pi && pi.IsReadOnly,
            Kind = MemberBindingKind.Option,
            CommandLineName = name,
            Description = description,
            Aliases = aliases,
            Required = required,
            Order = order,
        };
    }

    private static ITypeSymbol? GetMemberType(ISymbol member) =>
        member switch
        {
            IFieldSymbol f => f.Type,
            IPropertySymbol p => p.Type,
            _ => null,
        };

    private static readonly SymbolDisplayFormat _fqnFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static string GetTypeRef(ITypeSymbol type) =>
        type.ToDisplayString(_fqnFormat);

    // ─── Code generation ─────────────────────────────────────────────────────────

    private static void GenerateSource(
        SourceProductionContext spc,
        ImmutableArray<CommandTypeInfo> commandTypes,
        ImmutableArray<string> assemblyCommandPaths)
    {
        if (commandTypes.IsDefaultOrEmpty && assemblyCommandPaths.IsDefaultOrEmpty)
            return;

        // Place the generated class in the triaxis.CommandLine namespace so the
        // AddGeneratedCommands() extension method is in scope whenever the user
        // has `using triaxis.CommandLine;` (which they always do).
        const string ns = "triaxis.CommandLine";
        const string indent = "    ";

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");

        sb.AppendLine($"{indent}internal static partial class CommandLineSetup");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    /// <summary>");
        sb.AppendLine($"{indent}    /// Registers commands discovered at compile time via source generation,");
        sb.AppendLine($"{indent}    /// replacing the runtime reflection-based <c>AddCommandsFromAssembly</c>.");
        sb.AppendLine($"{indent}    /// </summary>");
        sb.AppendLine($"{indent}    public static global::triaxis.CommandLine.IToolBuilder AddGeneratedCommands(");
        sb.AppendLine($"{indent}        this global::triaxis.CommandLine.IToolBuilder __builder)");
        sb.AppendLine($"{indent}    {{");

        // Assembly-level command paths (creates nodes without handlers)
        foreach (var path in assemblyCommandPaths)
        {
            var parts = path.Split('/');
            var args = string.Join(", ", parts.Select(p => $"\"{EscapeString(p)}\""));
            sb.AppendLine($"{indent}        __builder.GetCommand({args});");
        }

        // DI registrations for all command types
        if (!commandTypes.IsDefaultOrEmpty)
        {
            sb.AppendLine($"{indent}        __builder.ConfigureServices(static (_, __services) =>");
            sb.AppendLine($"{indent}        {{");
            foreach (var ct in commandTypes)
                sb.AppendLine($"{indent}            __services.AddTransient<global::{ct.FullName}>();");
            sb.AppendLine($"{indent}        }});");
        }

        // Per-command registration
        foreach (var ct in commandTypes)
        {
            foreach (var cmdAttr in ct.Attributes)
            {
                sb.AppendLine();
                GenerateCommandBlock(sb, indent + "        ", ct, cmdAttr);
            }
        }

        sb.AppendLine();
        sb.AppendLine($"{indent}        return __builder;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine("}");

        spc.AddSource("CommandLineSetup.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void GenerateCommandBlock(
        StringBuilder sb,
        string indent,
        CommandTypeInfo ct,
        CommandAttrData attr)
    {
        var pathArgs = string.Join(", ", attr.Path.Select(p => $"\"{EscapeString(p)}\""));

        sb.AppendLine($"{indent}// Command: [{string.Join(", ", attr.Path)}]  ->  {ct.FullName}");
        sb.AppendLine($"{indent}{{");

        sb.AppendLine($"{indent}    var __cmd = __builder.GetCommand({pathArgs});");

        if (attr.Description != null)
            sb.AppendLine($"{indent}    __cmd.Description = \"{EscapeString(attr.Description)}\";");

        if (attr.Aliases != null)
        {
            foreach (var alias in attr.Aliases)
                sb.AppendLine($"{indent}    __cmd.AddAlias(\"{EscapeString(alias)}\");");
        }

        // Emit member setup (arguments and options)
        int symIndex = 0;
        EmitMemberSetup(sb, indent + "    ", ct.FullName, ct.Members, ref symIndex);

        // Binder lambda
        sb.AppendLine();
        sb.AppendLine($"{indent}    __cmd.Handler = new global::triaxis.CommandLine.GeneratedCommandHandler(");
        sb.AppendLine($"{indent}        typeof(global::{ct.FullName}),");
        sb.AppendLine($"{indent}        (__inst, __pr) =>");
        sb.AppendLine($"{indent}        {{");
        sb.AppendLine($"{indent}            var __typedInst = (global::{ct.FullName})__inst;");
        int bindIndex = 0;
        EmitBinderCode(sb, indent + "            ", ct.FullName, ct.Members, null, ref bindIndex);

        sb.AppendLine($"{indent}        }});");
        sb.AppendLine($"{indent}}}");
    }

    private static void EmitMemberSetup(
        StringBuilder sb,
        string indent,
        string typeFqn,
        IReadOnlyList<MemberBindingData> members,
        ref int symIndex)
    {
        foreach (var m in members)
        {
            switch (m.Kind)
            {
                case MemberBindingKind.Argument:
                    EmitArgumentSetup(sb, indent, typeFqn, m, ref symIndex);
                    break;
                case MemberBindingKind.Option:
                    EmitOptionSetup(sb, indent, typeFqn, m, ref symIndex);
                    break;
                case MemberBindingKind.Options:
                    // Pre-emit member field/property info for the nested object creation
                    EmitNestedOptionsInfo(sb, indent, typeFqn, m, ref symIndex);
                    EmitMemberSetup(sb, indent, m.NestedTypeFqn, m.NestedMembers, ref symIndex);
                    break;
            }
        }
    }

    private static void EmitArgumentSetup(
        StringBuilder sb,
        string indent,
        string typeFqn,
        MemberBindingData m,
        ref int symIndex)
    {
        var sym = $"__sym_{symIndex}";
        var typeRef = m.TypeFullName; // already fully qualified (global::...)
        var name = m.CommandLineName ?? m.MemberName;
        symIndex++;

        sb.AppendLine();
        sb.AppendLine($"{indent}// [{(m.IsField ? "field" : "property")}] {m.MemberName} -> Argument<{m.TypeFullName}> \"{name}\"");

        EmitMemberAccessSetup(sb, indent, typeFqn, m, symIndex - 1);

        sb.AppendLine($"{indent}var {sym} = new global::System.CommandLine.Argument<{typeRef}>(\"{EscapeString(name)}\")");
        sb.AppendLine($"{indent}{{");
        if (m.Description != null)
            sb.AppendLine($"{indent}    Description = \"{EscapeString(m.Description)}\",");
        sb.Append($"{indent}    Arity = ");
        if (m.Required == false)
            sb.AppendLine("global::System.CommandLine.ArgumentArity.ZeroOrOne,");
        else if (m.Required == true)
            sb.AppendLine("global::System.CommandLine.ArgumentArity.ExactlyOne,");
        else
            sb.AppendLine("global::System.CommandLine.ArgumentArity.ZeroOrOne,");
        sb.AppendLine($"{indent}}};");
        sb.AppendLine($"{indent}__cmd.AddArgument({sym});");
    }

    private static void EmitOptionSetup(
        StringBuilder sb,
        string indent,
        string typeFqn,
        MemberBindingData m,
        ref int symIndex)
    {
        var sym = $"__sym_{symIndex}";
        var typeRef = m.TypeFullName; // already fully qualified (global::...)
        var name = m.CommandLineName ?? m.MemberName;
        symIndex++;

        sb.AppendLine();
        sb.AppendLine($"{indent}// [{(m.IsField ? "field" : "property")}] {m.MemberName} -> Option<{m.TypeFullName}> \"{name}\"");

        EmitMemberAccessSetup(sb, indent, typeFqn, m, symIndex - 1);

        // Build names array (name + aliases)
        string namesExpr;
        if (m.Aliases != null && m.Aliases.Length > 0)
        {
            var all = new[] { name }.Concat(m.Aliases).Select(a => $"\"{EscapeString(a)}\"");
            namesExpr = $"new string[] {{ {string.Join(", ", all)} }}";
        }
        else
        {
            namesExpr = $"\"{EscapeString(name)}\"";
        }

        sb.AppendLine($"{indent}var {sym} = new global::System.CommandLine.Option<{typeRef}>({namesExpr})");
        sb.AppendLine($"{indent}{{");
        if (m.Description != null)
            sb.AppendLine($"{indent}    Description = \"{EscapeString(m.Description)}\",");
        if (m.Required == true)
            sb.AppendLine($"{indent}    IsRequired = true,");
        sb.AppendLine($"{indent}}};");
        sb.AppendLine($"{indent}__cmd.AddOption({sym});");
    }

    private static void EmitNestedOptionsInfo(
        StringBuilder sb,
        string indent,
        string typeFqn,
        MemberBindingData m,
        ref int symIndex)
    {
        EmitMemberAccessSetup(sb, indent, typeFqn, m, symIndex);
    }

    /// <summary>
    /// Emits a field/property accessor variable for use in the binder lambda.
    /// For private/protected members we emit a FieldInfo/PropertyInfo via reflection.
    /// For public members we can use direct access in the binder.
    /// </summary>
    private static void EmitMemberAccessSetup(
        StringBuilder sb,
        string indent,
        string typeFqn,
        MemberBindingData m,
        int idx)
    {
        if (m.IsPublic)
            return; // Direct access in binder, no need to store accessor

        var varName = $"__mi_{idx}";
        if (m.IsField)
        {
            // Member name is resolved from the Roslyn symbol, so it is always valid.
            sb.AppendLine($"{indent}var {varName} = typeof(global::{typeFqn})");
            sb.AppendLine($"{indent}    .GetField(\"{m.MemberName}\",");
            sb.AppendLine($"{indent}        global::System.Reflection.BindingFlags.Instance |");
            sb.AppendLine($"{indent}        global::System.Reflection.BindingFlags.NonPublic)");
            sb.AppendLine($"{indent}    ?? throw new global::System.InvalidOperationException(");
            sb.AppendLine($"{indent}        $\"Source-generated binder: field '{m.MemberName}' not found on {{typeof(global::{typeFqn}).FullName}}\");");
        }
        else
        {
            // Member name is resolved from the Roslyn symbol, so it is always valid.
            sb.AppendLine($"{indent}var {varName} = typeof(global::{typeFqn})");
            sb.AppendLine($"{indent}    .GetProperty(\"{m.MemberName}\",");
            sb.AppendLine($"{indent}        global::System.Reflection.BindingFlags.Instance |");
            sb.AppendLine($"{indent}        global::System.Reflection.BindingFlags.NonPublic)");
            sb.AppendLine($"{indent}    ?? throw new global::System.InvalidOperationException(");
            sb.AppendLine($"{indent}        $\"Source-generated binder: property '{m.MemberName}' not found on {{typeof(global::{typeFqn}).FullName}}\");");
        }
    }

    private static void EmitBinderCode(
        StringBuilder sb,
        string indent,
        string typeFqn,
        IReadOnlyList<MemberBindingData> members,
        string? targetExpr, // null = use __typedInst
        ref int bindIndex)
    {
        foreach (var m in members)
        {
            var sym = $"__sym_{bindIndex}";
            var mi = $"__mi_{bindIndex}";
            var typeRef = m.TypeFullName; // already fully qualified (global::...)
            var target = targetExpr ?? "__typedInst";
            bindIndex++;

            switch (m.Kind)
            {
                case MemberBindingKind.Argument:
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    var __r = __pr.FindResultFor({sym});");
                    sb.AppendLine($"{indent}    if (__r != null && __r.Tokens.Count > 0)");
                    sb.AppendLine($"{indent}    {{");
                    if (m.IsPublic)
                        sb.AppendLine($"{indent}        {target}.{m.MemberName} = __r.GetValueOrDefault<{typeRef}>();");
                    else
                        sb.AppendLine($"{indent}        {mi}.SetValue({target}, __r.GetValueOrDefault<{typeRef}>());");
                    sb.AppendLine($"{indent}    }}");
                    sb.AppendLine($"{indent}}}");
                    break;

                case MemberBindingKind.Option:
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    var __r = __pr.FindResultFor({sym});");
                    sb.AppendLine($"{indent}    if (__r != null && !__r.IsImplicit)");
                    sb.AppendLine($"{indent}    {{");
                    if (m.IsPublic)
                        sb.AppendLine($"{indent}        {target}.{m.MemberName} = __r.GetValueOrDefault<{typeRef}>();");
                    else
                        sb.AppendLine($"{indent}        {mi}.SetValue({target}, __r.GetValueOrDefault<{typeRef}>());");
                    sb.AppendLine($"{indent}    }}");
                    sb.AppendLine($"{indent}}}");
                    break;

                case MemberBindingKind.Options:
                    // Create the nested object if needed, then recurse
                    sb.AppendLine($"{indent}{{");
                    string nestedVar = $"__nested_{bindIndex}";
                    if (m.IsPublic)
                    {
                        sb.AppendLine($"{indent}    if ({target}.{m.MemberName} == null)");
                        sb.AppendLine($"{indent}        {target}.{m.MemberName} = new {m.NestedTypeFqn}();");
                        sb.AppendLine($"{indent}    var {nestedVar} = ({m.NestedTypeFqn}){target}.{m.MemberName};");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}    var {nestedVar}Raw = {mi}.GetValue({target});");
                        sb.AppendLine($"{indent}    if ({nestedVar}Raw == null)");
                        sb.AppendLine($"{indent}    {{");
                        sb.AppendLine($"{indent}        {nestedVar}Raw = new {m.NestedTypeFqn}();");
                        sb.AppendLine($"{indent}        {mi}.SetValue({target}, {nestedVar}Raw);");
                        sb.AppendLine($"{indent}    }}");
                        sb.AppendLine($"{indent}    var {nestedVar} = ({m.NestedTypeFqn}){nestedVar}Raw;");
                    }
                    EmitBinderCode(sb, indent + "    ", m.NestedTypeFqn, m.NestedMembers, nestedVar, ref bindIndex);
                    sb.AppendLine($"{indent}}}");
                    break;
            }
        }
    }

    private static string EscapeString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
