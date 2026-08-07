namespace triaxis.CommandLine.SourceGenerator;

using System.CodeDom.Compiler;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Emits <c>IObjectDescriptor</c> implementations for every type a command can output,
/// walked transitively through nested members.
/// </summary>
/// <remarks>
/// The reflective descriptor resolves shape through <c>TypeDescriptor.GetProperties</c>
/// and <c>Activator.CreateInstance(MakeGenericType(...))</c> — the first is annotated
/// <c>RequiresUnreferencedCode</c> and cannot be made trim-safe, the second cannot
/// instantiate over a value type that was never statically rooted under AOT. Both
/// disappear when the descriptor is a compile-time artifact.
///
/// Field ordering and visibility are also resolved here rather than at run time, so the
/// emitted field array is already in final order.
/// </remarks>
[Generator]
public class ObjectDescriptorGenerator : IIncrementalGenerator
{
    private const string DescriptorNamespace = "triaxis.CommandLine.Generated";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "triaxis.CommandLine.CommandAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractOutputTypes(ctx, ct))
            .Where(static m => !m.IsDefaultOrEmpty)
            .Collect()
            .Select(static (batches, _) => Deduplicate(batches));

        context.RegisterSourceOutput(models, static (spc, descriptors) =>
        {
            if (descriptors.IsDefaultOrEmpty)
            {
                return;
            }

            spc.AddSource("ObjectDescriptors.g.cs", Generate(descriptors));
        });
    }

    // --- extraction

    private static ImmutableArray<DescriptorModel> ExtractOutputTypes(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol command)
        {
            return ImmutableArray<DescriptorModel>.Empty;
        }

        var collected = new Dictionary<string, DescriptorModel>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in command.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();

            if (method.Name is not ("Execute" or "ExecuteAsync") || method.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (UnwrapOutputType(method.ReturnType) is { } element)
            {
                Describe(element, collected, visiting, ct);
            }
        }

        return collected.Count == 0
            ? ImmutableArray<DescriptorModel>.Empty
            : collected.Values.OrderBy(d => d.TypeFqn, StringComparer.Ordinal).ToImmutableArray();
    }

    /// Peels Task&lt;&gt;, IAsyncEnumerable&lt;&gt; and IEnumerable&lt;&gt; down to the
    /// type a formatter would render one row from.
    private static ITypeSymbol? UnwrapOutputType(ITypeSymbol type)
    {
        for (int depth = 0; depth < 4; depth++)
        {
            if (type.SpecialType is SpecialType.System_Void or SpecialType.System_Int32
                || type.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
            {
                return null;
            }

            if (type is not INamedTypeSymbol { IsGenericType: true } named)
            {
                break;
            }

            var definition = named.OriginalDefinition.ToDisplayString();
            if (definition is "System.Threading.Tasks.Task<TResult>"
                or "System.Threading.Tasks.ValueTask<TResult>"
                or "System.Collections.Generic.IAsyncEnumerable<T>"
                or "System.Collections.Generic.IEnumerable<T>"
                or "System.Collections.Generic.IList<T>"
                or "System.Collections.Generic.IReadOnlyList<T>"
                or "System.Collections.Generic.ICollection<T>"
                or "System.Collections.Generic.List<T>")
            {
                type = named.TypeArguments[0];
                continue;
            }

            break;
        }

        if (type is IArrayTypeSymbol array)
        {
            type = array.ElementType;
        }

        return CanDescribe(type) ? type : null;
    }

    private static void Describe(ITypeSymbol type, Dictionary<string, DescriptorModel> collected,
        HashSet<string> visiting, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!CanDescribe(type))
        {
            return;
        }

        var fqn = type.ToDisplayString(FqnFormat);

        // a self-referencing graph would otherwise recurse forever
        if (collected.ContainsKey(fqn) || !visiting.Add(fqn))
        {
            return;
        }

        var nested = new List<ITypeSymbol>();
        var fields = CollectFields(type, prefix: "", nested);

        // Registered before descending, so a member that points back at this type — or at
        // anything already on the stack — terminates instead of recursing forever. A
        // fieldless descriptor is worse than none: it would win the registry lookup and
        // render the value as an empty mapping, where deferring to the run-time fallback
        // still has a chance of describing it.
        if (fields.Count > 0)
        {
            collected[fqn] = new DescriptorModel(fqn, Order(fields).ToImmutableArray());
        }

        foreach (var member in nested)
        {
            Describe(member, collected, visiting, ct);
        }

        visiting.Remove(fqn);
    }

    /// <summary>
    /// Builds the field list for a type, flattening tuple elements into the same list so
    /// ordering anchors can reach across elements the way <c>TupleObjectDescriptor</c>
    /// does at run time.
    /// </summary>
    /// <param name="prefix">Member path accumulated from enclosing tuple elements.</param>
    /// <param name="nested">Collects member types that need descriptors of their own.</param>
    private static List<FieldModel> CollectFields(ITypeSymbol type, string prefix, List<ITypeSymbol> nested)
    {
        var fields = new List<FieldModel>();

        if (type is INamedTypeSymbol { IsTupleType: true } tuple)
        {
            foreach (var element in tuple.TupleElements)
            {
                // element.Name is the declared name for a named tuple, "ItemN" otherwise;
                // both are valid member accesses on the tuple type
                var access = prefix + element.Name;

                if (Flattens(element.Type))
                {
                    fields.AddRange(CollectFields(element.Type, access + ".", nested));
                }
                else
                {
                    // A scalar element has no members to lift, so it becomes one field
                    // named after the element itself. The reflective descriptor instead
                    // describes the scalar's own properties, which turns (string, int)
                    // into a single "Length" column.
                    fields.Add(MakeField(element.Name, element, element.Type, access));
                    Collect(element.Type, nested);
                }
            }

            return fields;
        }

        foreach (var property in CollectProperties(type))
        {
            fields.Add(MakeField(property.Name, property, property.Type, prefix + property.Name));
            Collect(property.Type, nested);
        }

        return fields;
    }

    /// Whether a tuple element contributes its own members rather than becoming one field.
    private static bool Flattens(ITypeSymbol type)
        => type is INamedTypeSymbol { IsTupleType: true }
        || (IsDescribable(type) && CollectProperties(type).Any());

    private static void Collect(ITypeSymbol memberType, List<ITypeSymbol> nested)
    {
        var element = memberType is IArrayTypeSymbol array ? array.ElementType : UnwrapCollection(memberType);
        nested.Add(element ?? memberType);
    }

    private static FieldModel MakeField(string name, ISymbol declaration, ITypeSymbol type, string access)
    {
        var attr = declaration.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "triaxis.CommandLine.ObjectOutput.ObjectOutputAttribute");

        return new FieldModel(
            Name: name,
            Title: GetAttributeString(declaration, "System.ComponentModel.DisplayNameAttribute", "DisplayName") ?? name,
            TypeFqn: type.ToDisplayString(FqnFormat),
            Access: access,
            Visibility: ResolveVisibility(attr, declaration),
            Format: GetNamedArgument(attr, "Format") as string,
            Before: GetNamedArgument(attr, "Before") as string,
            After: GetNamedArgument(attr, "After") as string);
    }

    /// Types this generator will emit a descriptor for — tuples included, unlike the
    /// member-recursion test which only cares about ordinary described types.
    private static bool CanDescribe(ITypeSymbol type)
        => type is INamedTypeSymbol { IsTupleType: true } || IsDescribable(type);

    private static IEnumerable<IPropertySymbol> CollectProperties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // declaration order first, base members after, matching the reflective descriptor
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.DeclaredAccessibility == Accessibility.Public
                    && !property.IsStatic
                    && !property.IsIndexer
                    && property.GetMethod is not null
                    && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    private static ITypeSymbol? UnwrapCollection(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        foreach (var iface in (type as INamedTypeSymbol)?.AllInterfaces ?? ImmutableArray<INamedTypeSymbol>.Empty)
        {
            if (iface.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            {
                return iface.TypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a type's shape is knowable at compile time. Interfaces, abstract types and
    /// <c>object</c> are left to the run-time fallback because the value's actual type is
    /// only known once it exists.
    /// </summary>
    private static bool IsDescribable(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.IsAbstract || named.TypeKind is TypeKind.Interface or TypeKind.Enum)
        {
            return false;
        }

        if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Delegate)
        {
            return false;
        }

        // Tuples expose their elements as fields rather than properties, and
        // TupleObjectDescriptor flattens each element's own descriptor rather than
        // describing the tuple itself. Their display string is "(A, B)", so the System.*
        // check below does not catch them.
        if (named.IsTupleType)
        {
            return false;
        }

        // Nullable<T> arrives boxed as T, and the BCL's own types are scalars or
        // collections rather than records to describe
        var fqn = named.ToDisplayString();
        if (fqn.StartsWith("System.", StringComparison.Ordinal))
        {
            return false;
        }

        return named.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;
    }

    private static string ResolveVisibility(AttributeData? attr, ISymbol property)
    {
        const string prefix = "global::triaxis.CommandLine.ObjectOutput.ObjectFieldVisibility.";

        if (GetNamedArgument(attr, "Visibility") is int explicitValue)
        {
            return prefix + explicitValue switch { 1 => "Extended", 2 => "Internal", _ => "Standard" };
        }

        if (attr?.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is int positional)
        {
            return prefix + positional switch { 1 => "Extended", 2 => "Internal", _ => "Standard" };
        }

        // [Browsable(false)] demotes a member, matching PropertyDescriptor.IsBrowsable
        var browsable = property.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.BrowsableAttribute");
        if (browsable?.ConstructorArguments.Length == 1 && browsable.ConstructorArguments[0].Value is false)
        {
            return prefix + "Extended";
        }

        return prefix + "Standard";
    }

    private static object? GetNamedArgument(AttributeData? attr, string name)
        => attr?.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value;

    private static string? GetAttributeString(ISymbol symbol, string attributeFqn, string _)
        => symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeFqn)?
            .ConstructorArguments.FirstOrDefault().Value as string;

    /// Port of the run-time <c>Ordered()</c> pass, applied once at generation time.
    private static List<FieldModel> Order(List<FieldModel> fields)
    {
        var main = new List<FieldModel>();
        var before = new List<FieldModel>();
        var after = new List<FieldModel>();

        foreach (var f in fields)
        {
            (f.Before is not null ? before : f.After is not null ? after : main).Add(f);
        }

        if (before.Count == 0 && after.Count == 0)
        {
            return main;
        }

        var result = new List<FieldModel>(fields.Count);

        foreach (var f in main)
        {
            Add(f);
        }

        // anchors naming a field that does not exist still have to be emitted
        while (before.Count > 0)
        {
            var f = before[0];
            before.RemoveAt(0);
            Add(f);
        }

        while (after.Count > 0)
        {
            var f = after[0];
            after.RemoveAt(0);
            Add(f);
        }

        return result;

        void Add(FieldModel field)
        {
            // each insertion can attract further anchors, so rescan from the start
            for (int i = 0; i < before.Count; i++)
            {
                if (before[i].Before == field.Name)
                {
                    var anchored = before[i];
                    before.RemoveAt(i);
                    Add(anchored);
                    i = -1;
                }
            }

            result.Add(field);

            for (int i = 0; i < after.Count; i++)
            {
                if (after[i].After == field.Name)
                {
                    var anchored = after[i];
                    after.RemoveAt(i);
                    Add(anchored);
                    i = -1;
                }
            }
        }
    }

    private static ImmutableArray<DescriptorModel> Deduplicate(ImmutableArray<ImmutableArray<DescriptorModel>> batches)
    {
        var merged = new Dictionary<string, DescriptorModel>(StringComparer.Ordinal);

        foreach (var batch in batches)
        {
            foreach (var descriptor in batch)
            {
                merged[descriptor.TypeFqn] = descriptor;
            }
        }

        return merged.Values.OrderBy(d => d.TypeFqn, StringComparer.Ordinal).ToImmutableArray();
    }

    // --- emission

    private static string Generate(ImmutableArray<DescriptorModel> descriptors)
    {
        var sw = new StringWriter();
        var w = new IndentedTextWriter(sw);

        w.WriteLine("// <auto-generated/>");
        w.WriteLine("#nullable enable");
        w.WriteLine("#pragma warning disable CS8603 // a nullable member read through a non-nullable accessor");
        w.WriteLine();
        w.WriteLine("using System;");
        w.WriteLine("using System.Collections.Generic;");
        w.WriteLine("using System.Reflection;");
        w.WriteLine("using System.Runtime.CompilerServices;");
        w.WriteLine("using triaxis.CommandLine.ObjectOutput;");
        w.WriteLine("using triaxis.Reflection;");
        w.WriteLine();

        w.Block($"namespace {DescriptorNamespace}", () =>
        {
            w.Block("internal static class GeneratedObjectDescriptors", () =>
            {
                w.WriteLine("[ModuleInitializer]");
                w.Block("internal static void Register()", () =>
                {
                    w.WriteLine("ObjectDescriptorRegistry.Register(TryGet);");
                });
                w.WriteLine();

                w.Block("private static IObjectDescriptor? TryGet(Type type)", () =>
                {
                    for (int i = 0; i < descriptors.Length; i++)
                    {
                        w.Block($"if (type == typeof({descriptors[i].TypeFqn}))", () =>
                        {
                            w.WriteLine($"return Descriptor_{i}.Instance;");
                        });
                    }
                    w.WriteLine("return null;");
                });
                w.WriteLine();

                for (int i = 0; i < descriptors.Length; i++)
                {
                    EmitDescriptor(w, descriptors[i], i);
                    w.WriteLine();
                }
            });
        });

        w.Flush();
        return sw.ToString();
    }

    private static void EmitDescriptor(IndentedTextWriter w, DescriptorModel model, int index)
    {
        w.Block($"internal sealed class Descriptor_{index} : IObjectDescriptor", () =>
        {
            w.WriteLine($"internal static readonly Descriptor_{index} Instance = new Descriptor_{index}();");
            w.WriteLine($"private Descriptor_{index}() {{ }}");
            w.WriteLine();

            var fieldList = string.Join(", ", model.Fields.Select((_, i) => $"new Field_{i}()"));
            w.WriteLine($"private static readonly IObjectField[] s_fields = new IObjectField[] {{ {fieldList} }};");
            w.WriteLine("public IReadOnlyList<IObjectField> Fields => s_fields;");

            for (int i = 0; i < model.Fields.Length; i++)
            {
                w.WriteLine();
                EmitField(w, model, model.Fields[i], i);
            }
        });
    }

    private static void EmitField(IndentedTextWriter w, DescriptorModel owner, FieldModel field, int index)
    {
        var t = field.TypeFqn;

        w.Block($"private sealed class Field_{index} : IObjectField, IObjectField<{t}>, IPropertyGetter<{t}>", () =>
        {
            w.WriteLine($"public string Name => {FormatLiteral(field.Name)};");
            w.WriteLine($"public string Title => {FormatLiteral(field.Title)};");
            w.WriteLine($"public ObjectFieldVisibility Visibility => {field.Visibility};");
            w.WriteLine($"public Type Type => typeof({t});");
            w.WriteLine($"public string? Format => {(field.Format is null ? "null" : FormatLiteral(field.Format))};");
            w.WriteLine("public IPropertyGetter Accessor => this;");
            w.WriteLine($"IPropertyGetter<{t}> IObjectField<{t}>.Accessor => this;");
            w.WriteLine();
            w.WriteLine("// generated descriptors carry no PropertyInfo — the whole point is not to need one");
            w.WriteLine($"public PropertyInfo Property => throw new NotSupportedException(\"Generated descriptors do not expose PropertyInfo.\");");
            w.WriteLine();
            w.WriteLine($"public {t} Get(object target) => (({owner.TypeFqn})target).{field.Access};");
            w.WriteLine("object? IPropertyGetter.Get(object target) => Get(target);");
        });
    }

    private static string FormatLiteral(string value)
    {
        var sb = new StringBuilder("\"");

        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    // --- models (value-equatable so the incremental pipeline can cache)

    private sealed record DescriptorModel(string TypeFqn, ImmutableArray<FieldModel> Fields)
    {
        public bool Equals(DescriptorModel? other)
            => other is not null && TypeFqn == other.TypeFqn && Fields.SequenceEqual(other.Fields);

        public override int GetHashCode() => TypeFqn.GetHashCode();
    }

    /// <param name="Access">
    /// Member path from the described instance, e.g. <c>City</c> for a plain property or
    /// <c>Item1.City</c> for a field lifted out of a tuple element.
    /// </param>
    private sealed record FieldModel(
        string Name,
        string Title,
        string TypeFqn,
        string Access,
        string Visibility,
        string? Format,
        string? Before,
        string? After);
}
