namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

/// <summary>
/// Keeps a parent command's own arguments and options out of its subcommands — both when
/// parsing a command line and when rendering a subcommand's help.
/// </summary>
/// <remarks>
/// System.CommandLine's tokenizer is arity-blind: any token matching a subcommand name
/// switches to that subcommand regardless of how many of the parent's positional arguments
/// are still unfilled, so <c>tool a b sub c d</c> silently binds <c>a b</c> to the parent
/// and leaves only <c>c d</c> for <c>sub</c>. Anything appearing before that switch is read
/// against the parent, which makes its non-recursive options bindable there too —
/// <c>tool --parent x sub</c> sets an option the invoked command does not declare, while
/// the same option written after the subcommand is correctly rejected. Its help renderer
/// takes the same view of the ancestor chain's arguments, listing them in the subcommand's
/// usage line and arguments section (options it already scopes correctly). None of it
/// offers a hook, so it is all corrected from the outside: a validator on every command
/// below one that declares arguments or scoped options reports what was swallowed as
/// unrecognized, and the help action hides the ancestors' arguments for the render.
/// </remarks>
static class SubcommandArgumentGuard
{
    public static void Install(Command root)
        => Install(root, ancestorIsShadowed: false);

    private static void Install(Command command, bool ancestorIsShadowed)
    {
        if (ancestorIsShadowed)
        {
            // Only the innermost command's validators run, hence the walk up the
            // ancestor chain inside Validate.
            command.Validators.Add(Validate);
        }

        // Help is resolved by walking up to the nearest HelpOption, so wrapping has to
        // reach the ones on unshadowed ancestors too — the root's in particular.
        foreach (var option in command.Options)
        {
            if (option is HelpOption help &&
                help.Action is SynchronousCommandLineAction inner and not ScopedHelpAction)
            {
                help.Action = new ScopedHelpAction(inner);
            }
        }

        var childrenAreShadowed = ancestorIsShadowed || Shadows(command);
        foreach (var subcommand in command.Subcommands)
        {
            Install(subcommand, childrenAreShadowed);
        }
    }

    /// <summary>
    /// Whether <paramref name="command"/> declares anything a subcommand's command line
    /// could bind by accident.
    /// </summary>
    private static bool Shadows(Command command)
        => command.Arguments.Count > 0 || command.Options.Any(IsScoped);

    /// <summary>
    /// A recursive option is declared for the whole subtree, so binding it below its owner
    /// is the entire point. An option carrying an <see cref="Option.Action"/> binds no
    /// value — it replaces the invocation (<c>--help</c>, <c>--version</c>, an
    /// <c>[ActionOption]</c> method) — so there is nothing for a subcommand to swallow.
    /// </summary>
    private static bool IsScoped(Option option)
        => !option.Recursive && option.Action is null;

    private static void Validate(CommandResult result)
    {
        for (var ancestor = result.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is not CommandResult ancestorCommand)
            {
                continue;
            }

            foreach (var argument in ancestorCommand.Command.Arguments)
            {
                if (result.GetResult(argument) is { } argumentResult)
                {
                    foreach (var token in argumentResult.Tokens)
                    {
                        result.AddError($"Unrecognized command or argument '{token.Value}'.");
                    }
                }
            }

            foreach (var option in ancestorCommand.Command.Options)
            {
                // The identifier is the misplaced thing; its value tokens are only there
                // because it was, so one error per option keeps the report readable.
                if (IsScoped(option) &&
                    result.GetResult(option) is { Implicit: false, IdentifierToken: { } token })
                {
                    result.AddError($"Unrecognized command or argument '{token.Value}'.");
                }
            }
        }
    }

    /// <summary>
    /// Renders help for a command with its ancestors' positional arguments hidden.
    /// Also covers the help System.CommandLine prints after a parse error, which reaches
    /// the same <see cref="HelpOption.Action"/>.
    /// </summary>
    private sealed class ScopedHelpAction(SynchronousCommandLineAction inner) : SynchronousCommandLineAction
    {
        public override bool Terminating => inner.Terminating;
        public override bool ClearsParseErrors => inner.ClearsParseErrors;

        public override int Invoke(ParseResult parseResult)
        {
            List<Argument> hidden = [];
            for (var command = Parent(parseResult.CommandResult.Command); command is not null; command = Parent(command))
            {
                foreach (var argument in command.Arguments)
                {
                    if (!argument.Hidden)
                    {
                        argument.Hidden = true;
                        hidden.Add(argument);
                    }
                }
            }

            try
            {
                return inner.Invoke(parseResult);
            }
            finally
            {
                foreach (var argument in hidden)
                {
                    argument.Hidden = false;
                }
            }
        }

        private static Command? Parent(Command command)
            => command.Parents.OfType<Command>().FirstOrDefault();
    }
}
