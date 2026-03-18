using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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

        var sw = new StringWriter();
        var writer = new IndentedTextWriter(sw, "    ");

        writer.WriteLine("// <auto-generated/>");
        writer.WriteLine("#nullable enable");
        writer.WriteLine();
        writer.WriteLine("using Microsoft.Extensions.DependencyInjection;");
        writer.WriteLine("using System.Reflection;");
        writer.WriteLine();
        writer.WriteLine($"namespace {ns}");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("internal static partial class CommandLineSetup");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// Registers commands discovered at compile time via source generation,");
        writer.WriteLine("/// replacing the runtime reflection-based <c>AddCommandsFromAssembly</c>.");
        writer.WriteLine("/// </summary>");
        writer.WriteLine("public static global::triaxis.CommandLine.IToolBuilder AddGeneratedCommands(");
        writer.Indent++;
        writer.WriteLine("this global::triaxis.CommandLine.IToolBuilder __builder)");
        writer.Indent--;
        writer.WriteLine("{");
        writer.Indent++;

        // Assembly-level command paths (creates nodes without handlers)
        foreach (var path in assemblyCommandPaths)
        {
            var parts = path.Split('/');
            var args = string.Join(", ", parts.Select(p => $"\"{EscapeString(p)}\""));
            writer.WriteLine($"__builder.GetCommand({args});");
        }

        // DI registrations for all command types
        if (!commandTypes.IsDefaultOrEmpty)
        {
            writer.WriteLine("__builder.ConfigureServices(static (_, __services) =>");
            writer.WriteLine("{");
            writer.Indent++;
            foreach (var ct in commandTypes)
                writer.WriteLine($"__services.AddTransient<global::{ct.FullName}>();");
            writer.Indent--;
            writer.WriteLine("});");
        }

        // Per-command registration
        foreach (var ct in commandTypes)
        {
            foreach (var cmdAttr in ct.Attributes)
            {
                writer.WriteLine();
                GenerateCommandBlock(writer, ct, cmdAttr);
            }
        }

        writer.WriteLine();
        writer.WriteLine("return __builder;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");

        spc.AddSource("CommandLineSetup.g.cs", SourceText.From(sw.ToString(), Encoding.UTF8));
    }

    private static void GenerateCommandBlock(
        IndentedTextWriter writer,
        CommandTypeInfo ct,
        CommandAttrData attr)
    {
        var pathArgs = string.Join(", ", attr.Path.Select(p => $"\"{EscapeString(p)}\""));

        writer.WriteLine($"// Command: [{string.Join(", ", attr.Path)}]  ->  {ct.FullName}");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine($"var __cmd = __builder.GetCommand({pathArgs});");

        if (attr.Description != null)
            writer.WriteLine($"__cmd.Description = \"{EscapeString(attr.Description)}\";");

        if (attr.Aliases != null)
        {
            foreach (var alias in attr.Aliases)
                writer.WriteLine($"__cmd.AddAlias(\"{EscapeString(alias)}\");");
        }

        // Emit member setup (arguments and options)
        int symIndex = 0;
        EmitMemberSetup(writer, ct.FullName, ct.Members, ref symIndex);

        // Binder lambda
        writer.WriteLine();
        writer.WriteLine("__cmd.Handler = new global::triaxis.CommandLine.GeneratedCommandHandler(");
        writer.Indent++;
        writer.WriteLine($"typeof(global::{ct.FullName}),");
        writer.WriteLine("(__inst, __pr) =>");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var __typedInst = (global::{ct.FullName})__inst;");
        int bindIndex = 0;
        EmitBinderCode(writer, ct.FullName, ct.Members, null, ref bindIndex);
        writer.Indent--;
        writer.WriteLine("});");
        writer.Indent--;

        writer.Indent--;
        writer.WriteLine("}");
    }

    private static void EmitMemberSetup(
        IndentedTextWriter writer,
        string typeFqn,
        IReadOnlyList<MemberBindingData> members,
        ref int symIndex)
    {
        foreach (var m in members)
        {
            switch (m.Kind)
            {
                case MemberBindingKind.Argument:
                    EmitArgumentSetup(writer, typeFqn, m, ref symIndex);
                    break;
                case MemberBindingKind.Option:
                    EmitOptionSetup(writer, typeFqn, m, ref symIndex);
                    break;
                case MemberBindingKind.Options:
                    // Pre-emit member field/property info for the nested object creation
                    EmitNestedOptionsInfo(writer, typeFqn, m, ref symIndex);
                    EmitMemberSetup(writer, m.NestedTypeFqn, m.NestedMembers, ref symIndex);
                    break;
            }
        }
    }

    private static void EmitArgumentSetup(
        IndentedTextWriter writer,
        string typeFqn,
        MemberBindingData m,
        ref int symIndex)
    {
        var sym = $"__sym_{symIndex}";
        var typeRef = m.TypeFullName; // already fully qualified (global::...)
        var name = m.CommandLineName ?? m.MemberName;
        symIndex++;

        writer.WriteLine();
        writer.WriteLine($"// [{(m.IsField ? "field" : "property")}] {m.MemberName} -> Argument<{m.TypeFullName}> \"{name}\"");

        EmitMemberAccessSetup(writer, typeFqn, m, symIndex - 1);

        var arity = m.Required switch
        {
            true => "global::System.CommandLine.ArgumentArity.ExactlyOne",
            false => "global::System.CommandLine.ArgumentArity.ZeroOrOne",
            _ => "global::System.CommandLine.ArgumentArity.ZeroOrOne",
        };

        writer.WriteLine($"var {sym} = new global::System.CommandLine.Argument<{typeRef}>(\"{EscapeString(name)}\")");
        writer.WriteLine("{");
        writer.Indent++;
        if (m.Description != null)
            writer.WriteLine($"Description = \"{EscapeString(m.Description)}\",");
        writer.WriteLine($"Arity = {arity},");
        writer.Indent--;
        writer.WriteLine("};");
        writer.WriteLine($"__cmd.AddArgument({sym});");
    }

    private static void EmitOptionSetup(
        IndentedTextWriter writer,
        string typeFqn,
        MemberBindingData m,
        ref int symIndex)
    {
        var sym = $"__sym_{symIndex}";
        var typeRef = m.TypeFullName; // already fully qualified (global::...)
        var name = m.CommandLineName ?? m.MemberName;
        symIndex++;

        writer.WriteLine();
        writer.WriteLine($"// [{(m.IsField ? "field" : "property")}] {m.MemberName} -> Option<{m.TypeFullName}> \"{name}\"");

        EmitMemberAccessSetup(writer, typeFqn, m, symIndex - 1);

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

        writer.WriteLine($"var {sym} = new global::System.CommandLine.Option<{typeRef}>({namesExpr})");
        writer.WriteLine("{");
        writer.Indent++;
        if (m.Description != null)
            writer.WriteLine($"Description = \"{EscapeString(m.Description)}\",");
        if (m.Required == true)
            writer.WriteLine("IsRequired = true,");
        writer.Indent--;
        writer.WriteLine("};");
        writer.WriteLine($"__cmd.AddOption({sym});");
    }

    private static void EmitNestedOptionsInfo(
        IndentedTextWriter writer,
        string typeFqn,
        MemberBindingData m,
        ref int symIndex)
    {
        EmitMemberAccessSetup(writer, typeFqn, m, symIndex);
    }

    /// <summary>
    /// Emits a field/property accessor variable for use in the binder lambda.
    /// For private/protected members we emit a FieldInfo/PropertyInfo via reflection.
    /// For public members we can use direct access in the binder.
    /// </summary>
    private static void EmitMemberAccessSetup(
        IndentedTextWriter writer,
        string typeFqn,
        MemberBindingData m,
        int idx)
    {
        if (m.IsPublic)
            return; // Direct access in binder, no need to store accessor

        var varName = $"__mi_{idx}";
        var (getMethod, memberKind) = m.IsField ? ("GetField", "field") : ("GetProperty", "property");

        // Member name is resolved from the Roslyn symbol, so it is always valid.
        writer.WriteLine($"var {varName} = typeof(global::{typeFqn})");
        writer.Indent++;
        writer.WriteLine($".{getMethod}(\"{m.MemberName}\",");
        writer.Indent++;
        writer.WriteLine("global::System.Reflection.BindingFlags.Instance |");
        writer.WriteLine("global::System.Reflection.BindingFlags.NonPublic)");
        writer.Indent--;
        writer.WriteLine("?? throw new global::System.InvalidOperationException(");
        writer.Indent++;
        writer.WriteLine($"$\"Source-generated binder: {memberKind} '{m.MemberName}' not found on {{typeof(global::{typeFqn}).FullName}}\");");
        writer.Indent--;
        writer.Indent--;
    }

    private static void EmitBinderCode(
        IndentedTextWriter writer,
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
                    writer.WriteLine("{");
                    writer.Indent++;
                    writer.WriteLine($"var __r = __pr.FindResultFor({sym});");
                    writer.WriteLine($"if (__r != null && __r.Tokens.Count > 0)");
                    writer.WriteLine("{");
                    writer.Indent++;
                    if (m.IsPublic)
                        writer.WriteLine($"{target}.{m.MemberName} = __r.GetValueOrDefault<{typeRef}>();");
                    else
                        writer.WriteLine($"{mi}.SetValue({target}, __r.GetValueOrDefault<{typeRef}>());");
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.Indent--;
                    writer.WriteLine("}");
                    break;

                case MemberBindingKind.Option:
                    writer.WriteLine("{");
                    writer.Indent++;
                    writer.WriteLine($"var __r = __pr.FindResultFor({sym});");
                    writer.WriteLine($"if (__r != null && !__r.IsImplicit)");
                    writer.WriteLine("{");
                    writer.Indent++;
                    if (m.IsPublic)
                        writer.WriteLine($"{target}.{m.MemberName} = __r.GetValueOrDefault<{typeRef}>();");
                    else
                        writer.WriteLine($"{mi}.SetValue({target}, __r.GetValueOrDefault<{typeRef}>());");
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.Indent--;
                    writer.WriteLine("}");
                    break;

                case MemberBindingKind.Options:
                    // Create the nested object if needed, then recurse
                    writer.WriteLine("{");
                    writer.Indent++;
                    string nestedVar = $"__nested_{bindIndex}";
                    if (m.IsPublic)
                    {
                        writer.WriteLine($"if ({target}.{m.MemberName} == null)");
                        writer.Indent++;
                        writer.WriteLine($"{target}.{m.MemberName} = new {m.NestedTypeFqn}();");
                        writer.Indent--;
                        writer.WriteLine($"var {nestedVar} = ({m.NestedTypeFqn}){target}.{m.MemberName};");
                    }
                    else
                    {
                        writer.WriteLine($"var {nestedVar}Raw = {mi}.GetValue({target});");
                        writer.WriteLine($"if ({nestedVar}Raw == null)");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine($"{nestedVar}Raw = new {m.NestedTypeFqn}();");
                        writer.WriteLine($"{mi}.SetValue({target}, {nestedVar}Raw);");
                        writer.Indent--;
                        writer.WriteLine("}");
                        writer.WriteLine($"var {nestedVar} = ({m.NestedTypeFqn}){nestedVar}Raw;");
                    }
                    EmitBinderCode(writer, m.NestedTypeFqn, m.NestedMembers, nestedVar, ref bindIndex);
                    writer.Indent--;
                    writer.WriteLine("}");
                    break;
            }
        }
    }

    private static string EscapeString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
