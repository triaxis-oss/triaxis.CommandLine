namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

/// <summary>
/// Keeps a command's positional arguments to the values actually meant for them: no
/// inputs belonging to a parent command, and no option the parser failed to recognise.
/// </summary>
/// <remarks>
/// System.CommandLine's tokenizer is arity-blind: any token matching a subcommand name
/// switches to that subcommand regardless of how many of the parent's positional arguments
/// are still unfilled, so <c>tool a b sub c d</c> silently binds <c>a b</c> to the parent
/// and leaves only <c>c d</c> for <c>sub</c>. Anything appearing before that switch is read
/// against the parent, which makes its non-recursive options bindable there too —
/// <c>tool --parent x sub</c> sets an option the invoked command does not declare, while
/// the same option written after the subcommand is correctly rejected. And a token it
/// recognises as neither command nor option falls through to the next unfilled positional
/// argument whatever it looks like, so a mistyped <c>--dyr-run</c> becomes a value.
/// Its help renderer takes the same view of the ancestor chain's arguments, listing them in
/// the subcommand's usage line and arguments section (options it already scopes correctly).
/// None of it offers a hook, so it is all corrected from the outside: a validator on every
/// command reports what was swallowed as unrecognized, and the help action hides the
/// ancestors' arguments for the render.
/// </remarks>
static class SubcommandArgumentGuard
{
    public static void Install(Command root, IReadOnlyList<string> args)
    {
        // Everything after the first `--` is a literal value by System.CommandLine's own
        // rule, and lands as the trailing positional tokens of the innermost command.
        // Counting them here is what lets the validator leave exactly those alone — the
        // token itself carries no usable marker, since `Token.Position` is internal.
        var literalCount = 0;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--")
            {
                literalCount = args.Count - i - 1;
                break;
            }
        }

        Install(root, literalCount);
    }

    private static void Install(Command command, int literalCount)
    {
        // Only the innermost command's validators run, hence the walk up the ancestor
        // chain inside Validate — and hence a validator on every command, since any of
        // them can be the innermost one.
        command.Validators.Add(result => Validate(result, literalCount));

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

        foreach (var subcommand in command.Subcommands)
        {
            Install(subcommand, literalCount);
        }
    }

    /// <summary>
    /// A recursive option is declared for the whole subtree, so binding it below its owner
    /// is the entire point. An option carrying an <see cref="Option.Action"/> binds no
    /// value — it replaces the invocation (<c>--help</c>, <c>--version</c>, an
    /// <c>[ActionOption]</c> method) — so there is nothing for a subcommand to swallow.
    /// </summary>
    private static bool IsScoped(Option option)
        => !option.Recursive && option.Action is null;

    /// <summary>
    /// A lone <c>-</c> is the conventional name for stdin, and a leading <c>-</c> on a
    /// number is a minus sign, so neither is a mistyped option.
    /// </summary>
    private static bool LooksLikeOption(string value)
        => value.Length > 1
            && value[0] == '-'
            && !char.IsDigit(value[1])
            && !(value[1] == '.' && value.Length > 2 && char.IsDigit(value[2]));

    private static void Validate(CommandResult result, int literalCount)
    {
        // A CommandResult's own tokens are exactly the ones bound to its positional
        // arguments — an option's values hang off the OptionResult instead — and they are
        // in command-line order, so dropping the last `literalCount` drops precisely what
        // followed the `--`.
        var tokens = result.Tokens;
        for (var i = 0; i < tokens.Count - literalCount; i++)
        {
            if (LooksLikeOption(tokens[i].Value))
            {
                result.AddError($"Unrecognized command or argument '{tokens[i].Value}'.");
            }
        }

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
