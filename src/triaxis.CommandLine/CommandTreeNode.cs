namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Invocation;

/// <summary>
/// Lightweight model describing a command tree node. Used by source-generated code
/// to describe the CLI structure without creating System.CommandLine types directly,
/// avoiding parent-tracking issues when merging trees.
/// </summary>
public class CommandTreeNode(string name)
{
    public string Name => name;
    public string? Description { get; set; }
    public string[]? Aliases { get; set; }
    public CommandLineAction? Action { get; set; }
    public List<ArgumentDefinition> Arguments { get; } = [];
    public List<OptionDefinition> Options { get; } = [];
    public List<CommandTreeNode> Subcommands { get; } = [];

    /// <summary>
    /// When <see langword="false"/> the node is not applied to the target tree.
    /// Source generators set this to a dynamic expression (e.g. an
    /// <c>OperatingSystem.IsWindows()</c> check) so commands annotated with
    /// <c>[SupportedOSPlatform]</c> only get registered on matching platforms.
    /// </summary>
    public bool IsSupported { get; set; } = true;

    /// <summary>
    /// Applies this node's properties to an existing <see cref="Command"/>,
    /// creating fresh System.CommandLine types for all arguments, options, and subcommands.
    /// Subcommands with matching names are merged recursively.
    /// </summary>
    public void ApplyTo(Command target)
    {
        if (Description is not null)
        {
            target.Description = Description;
        }

        if (Aliases is not null)
        {
            foreach (var alias in Aliases)
            {
                target.Aliases.Add(alias);
            }
        }

        if (Action is not null)
        {
            target.Action = Action;
        }

        foreach (var argDef in Arguments)
        {
            target.Arguments.Add(argDef.Create());
        }

        foreach (var optDef in Options)
        {
            target.Options.Add(optDef.Create());
        }

        foreach (var childNode in Subcommands)
        {
            if (!childNode.IsSupported)
            {
                continue;
            }

            var existing = target.Subcommands.FirstOrDefault(c =>
                string.Equals(c.Name, childNode.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                childNode.ApplyTo(existing);
            }
            else
            {
                var fresh = new Command(childNode.Name);
                childNode.ApplyTo(fresh);
                // Insert in sorted position (search from end — input is pre-sorted)
                var index = target.Subcommands.Count;
                while (index > 0 &&
                       string.Compare(target.Subcommands[index - 1].Name, fresh.Name, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    index--;
                }
                target.Subcommands.Insert(index, fresh);
            }
        }
    }
}

public abstract class ArgumentDefinition(string name)
{
    public string Name => name;
    public string? Description { get; set; }
    public ArgumentArity Arity { get; set; }

    public abstract Argument Create();
}

public class ArgumentDefinition<T>(string name) : ArgumentDefinition(name)
{
    public override Argument Create() =>
        new Argument<T>(Name) { Description = Description, Arity = Arity };
}

public abstract class OptionDefinition(string name, string[]? aliases = null)
{
    public string Name => name;
    public string[]? Aliases => aliases;
    public string? Description { get; set; }
    public bool Required { get; set; }
    public ArgumentArity Arity { get; set; }

    public abstract Option Create();
}

public class OptionDefinition<T>(string name, string[]? aliases = null) : OptionDefinition(name, aliases)
{
    public override Option Create()
    {
        var opt = Aliases is { Length: > 0 }
            ? new Option<T>(Name, Aliases)
            : new Option<T>(Name);
        opt.Description = Description;
        opt.Required = Required;
        opt.Arity = Arity;
        return opt;
    }
}
