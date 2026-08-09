namespace triaxis.CommandLine.Tests;

using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

public class ShadowState
{
    public string? First { get; set; }
    public string? Second { get; set; }
    public string? Third { get; set; }
    public string? ParentOpt { get; set; }
    public string? ChildOpt { get; set; }
    public bool Flag { get; set; }
    public string[]? Values { get; set; }
    public string? Ran { get; set; }
}

[Command("collect")]
public class CollectCommand
{
    [Inject]
    public ShadowState State { get; set; } = null!;

    [Argument]
    public string[] Values { get; set; } = [];

    public void Execute()
    {
        State.Ran = "collect";
        State.Values = Values;
    }
}

[Command("shadow")]
public class ShadowParentCommand
{
    [Inject]
    public ShadowState State { get; set; } = null!;

    [Argument]
    public string? First { get; set; }

    [Argument]
    public string? Second { get; set; }

    [Option("--parent-opt")]
    public string? ParentOpt { get; set; }

    [Option("--flag")]
    public bool Flag { get; set; }

    [ActionOption("--migrate")]
    public void Migrate() => State.Ran = "migrate";

    public void Execute()
    {
        State.Ran = "parent";
        State.First = First;
        State.Second = Second;
        State.ParentOpt = ParentOpt;
        State.Flag = Flag;
    }
}

[Command("shadow", "sub")]
public class ShadowChildCommand
{
    [Inject]
    public ShadowState State { get; set; } = null!;

    [Argument]
    public string? Third { get; set; }

    [Option("--child-opt")]
    public string? ChildOpt { get; set; }

    public void Execute()
    {
        State.Ran = "child";
        State.Third = Third;
        State.ChildOpt = ChildOpt;
    }
}

[Command("shadow", "sub", "deep")]
public class ShadowGrandchildCommand
{
    [Inject]
    public ShadowState State { get; set; } = null!;

    public void Execute() => State.Ran = "grandchild";
}

[TestFixture]
public class SubcommandArgumentGuardTests
{
    private static IToolBuilder CreateBuilder(params string[] args)
    {
        var builder = Tool.CreateBuilder(args);
        builder.AddCommandsFromAssembly(typeof(SubcommandArgumentGuardTests).Assembly);
        return builder;
    }

    private static string Help(params string[] args)
    {
        var output = new StringWriter();
        CreateBuilder(args).Parse().Invoke(new InvocationConfiguration { Output = output, Error = output });
        return output.ToString();
    }

    [Test]
    public void ParentArguments_AreRejected_WhenSubcommandIsInvoked()
    {
        var errors = CreateBuilder("shadow", "a", "b", "sub", "c").Parse().Errors;

        Assert.That(errors.Select(e => e.Message), Is.EqualTo(new[]
        {
            "Unrecognized command or argument 'a'.",
            "Unrecognized command or argument 'b'.",
        }));
    }

    [Test]
    public void ParentArguments_AreRejected_WhenOnlyPartiallyFilled()
    {
        var errors = CreateBuilder("shadow", "a", "sub", "c").Parse().Errors;

        Assert.That(errors.Select(e => e.Message),
            Is.EqualTo(new[] { "Unrecognized command or argument 'a'." }));
    }

    [Test]
    public void ParentArguments_AreRejected_AcrossTheWholeAncestorChain()
    {
        var errors = CreateBuilder("shadow", "a", "sub", "c", "deep").Parse().Errors;

        Assert.That(errors.Select(e => e.Message), Is.EqualTo(new[]
        {
            "Unrecognized command or argument 'c'.",
            "Unrecognized command or argument 'a'.",
        }));
    }

    [Test]
    public void ParentOptions_AreRejected_WhenSubcommandIsInvoked()
    {
        // Written after the subcommand ("shadow sub --parent-opt x") S.CL rejects this
        // itself; written before, it used to bind to the parent and stay silent.
        var errors = CreateBuilder("shadow", "--parent-opt", "x", "sub", "c").Parse().Errors;

        Assert.That(errors.Select(e => e.Message),
            Is.EqualTo(new[] { "Unrecognized command or argument '--parent-opt'." }));
    }

    [Test]
    public void ParentFlags_AreRejected_EvenThoughTheyCarryNoValueTokens()
    {
        var errors = CreateBuilder("shadow", "--flag", "sub", "c").Parse().Errors;

        Assert.That(errors.Select(e => e.Message),
            Is.EqualTo(new[] { "Unrecognized command or argument '--flag'." }));
    }

    [Test]
    public void RecursiveOptions_AreStillAcceptedAboveTheInvokedCommand()
    {
        var marker = new Option<string>("--marker") { Recursive = true };
        var builder = CreateBuilder("--marker", "m", "shadow", "sub", "c");
        builder.AddRecursiveOption(marker);

        var parseResult = builder.Parse();

        Assert.That(parseResult.Errors, Is.Empty);
        Assert.That(parseResult.GetValue(marker), Is.EqualTo("m"));
    }

    [Test]
    public async Task ActionOptions_AreNotRejected_TheyBindNoValue()
    {
        var state = new ShadowState();
        var builder = CreateBuilder("shadow", "--migrate", "sub", "c");
        builder.ConfigureServices(s => s.AddSingleton(state));

        Assert.That(builder.Parse().Errors, Is.Empty);
        Assert.That(await builder.RunAsync(), Is.Zero);
        Assert.That(state.Ran, Is.EqualTo("migrate"));
    }

    [Test]
    public async Task SubcommandOptions_StillBind()
    {
        var state = new ShadowState();
        var builder = CreateBuilder("shadow", "sub", "c", "--child-opt", "y");
        builder.ConfigureServices(s => s.AddSingleton(state));

        Assert.That(await builder.RunAsync(), Is.Zero);
        Assert.That(state.Ran, Is.EqualTo("child"));
        Assert.That(state.ChildOpt, Is.EqualTo("y"));
    }

    [Test]
    public async Task Parent_StillRunsWithItsOwnOptions()
    {
        var state = new ShadowState();
        var builder = CreateBuilder("shadow", "--parent-opt", "x", "--flag");
        builder.ConfigureServices(s => s.AddSingleton(state));

        Assert.That(await builder.RunAsync(), Is.Zero);
        Assert.That(state.Ran, Is.EqualTo("parent"));
        Assert.That(state.ParentOpt, Is.EqualTo("x"));
        Assert.That(state.Flag, Is.True);
    }

    [Test]
    public void UnknownOptions_AreRejected_InsteadOfBindingToAPositionalArgument()
    {
        var errors = CreateBuilder("collect", "--dyr-run").Parse().Errors;

        Assert.That(errors.Select(e => e.Message),
            Is.EqualTo(new[] { "Unrecognized command or argument '--dyr-run'." }));
    }

    [Test]
    public void UnknownOptions_AreRejected_BetweenGenuineValues()
    {
        var errors = CreateBuilder("collect", "a", "-x", "b").Parse().Errors;

        Assert.That(errors.Select(e => e.Message),
            Is.EqualTo(new[] { "Unrecognized command or argument '-x'." }));
    }

    [Test]
    public void UnknownOptions_AreRejected_OnSubcommandsToo()
    {
        var errors = CreateBuilder("shadow", "sub", "--typo").Parse().Errors;

        Assert.That(errors.Select(e => e.Message),
            Is.EqualTo(new[] { "Unrecognized command or argument '--typo'." }));
    }

    [Test]
    public async Task AfterDoubleDash_OptionLikeValuesBind()
    {
        var state = new ShadowState();
        var builder = CreateBuilder("collect", "--", "--typo", "-x");
        builder.ConfigureServices(s => s.AddSingleton(state));

        Assert.That(await builder.RunAsync(), Is.Zero);
        Assert.That(state.Values, Is.EqualTo(new[] { "--typo", "-x" }));
    }

    [Test]
    public void DoubleDash_ExemptsOnlyWhatFollowsIt()
    {
        var errors = CreateBuilder("collect", "--typo", "--", "-x").Parse().Errors;

        Assert.That(errors.Select(e => e.Message),
            Is.EqualTo(new[] { "Unrecognized command or argument '--typo'." }));
    }

    [Test]
    public async Task NegativeNumbersAndLoneDash_AreNotOptions()
    {
        var state = new ShadowState();
        var builder = CreateBuilder("collect", "-5", "-.5", "-");
        builder.ConfigureServices(s => s.AddSingleton(state));

        Assert.That(await builder.RunAsync(), Is.Zero);
        Assert.That(state.Values, Is.EqualTo(new[] { "-5", "-.5", "-" }));
    }

    [Test]
    public void OptionValuesStartingWithDash_AreStillAccepted()
    {
        // The check covers a command's positional arguments; an option's own value hangs
        // off its OptionResult and is none of the guard's business.
        var errors = CreateBuilder("shadow", "--parent-opt", "-x").Parse().Errors;

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task ShadowedArguments_FailTheRunWithoutExecutingAnything()
    {
        var state = new ShadowState();
        var builder = CreateBuilder("shadow", "a", "b", "sub", "c");
        builder.ConfigureServices(s => s.AddSingleton(state));

        Assert.That(await builder.RunAsync(), Is.Not.Zero);
        Assert.That(state.Ran, Is.Null);
    }

    [Test]
    public async Task Subcommand_StillRunsWhenParentArgumentsAreOmitted()
    {
        var state = new ShadowState();
        var builder = CreateBuilder("shadow", "sub", "c");
        builder.ConfigureServices(s => s.AddSingleton(state));

        Assert.That(await builder.RunAsync(), Is.Zero);
        Assert.That(state.Ran, Is.EqualTo("child"));
        Assert.That(state.Third, Is.EqualTo("c"));
    }

    [Test]
    public async Task Parent_StillRunsWithItsOwnArguments()
    {
        var state = new ShadowState();
        var builder = CreateBuilder("shadow", "a", "b");
        builder.ConfigureServices(s => s.AddSingleton(state));

        Assert.That(await builder.RunAsync(), Is.Zero);
        Assert.That(state.Ran, Is.EqualTo("parent"));
        Assert.That(state.First, Is.EqualTo("a"));
        Assert.That(state.Second, Is.EqualTo("b"));
    }

    [Test]
    public void SubcommandHelp_OmitsTheParentArguments()
    {
        var help = Help("shadow", "sub", "--help");

        Assert.That(help, Does.Contain("<THIRD>"));
        Assert.That(help, Does.Not.Contain("<FIRST>"));
        Assert.That(help, Does.Not.Contain("<SECOND>"));
    }

    [Test]
    public void SubcommandHelp_OmitsArgumentsFromEveryAncestor()
    {
        var help = Help("shadow", "sub", "deep", "--help");

        Assert.That(help, Does.Not.Contain("<FIRST>"));
        Assert.That(help, Does.Not.Contain("<THIRD>"));
    }

    [Test]
    public void ParentHelp_StillListsItsOwnArguments_AfterASubcommandHelpRender()
    {
        // Hiding mutates the shared tree, so it has to be undone once the render is over.
        var builder = CreateBuilder("shadow", "sub", "--help");
        var output = new StringWriter();
        builder.Parse().Invoke(new InvocationConfiguration { Output = output, Error = output });

        var parentHelp = new StringWriter();
        builder.RootCommand.Parse(["shadow", "--help"])
            .Invoke(new InvocationConfiguration { Output = parentHelp, Error = parentHelp });

        Assert.That(parentHelp.ToString(), Does.Contain("<FIRST>"));
        Assert.That(parentHelp.ToString(), Does.Contain("<SECOND>"));
    }

    [Test]
    public void ParseErrorHelp_AlsoOmitsTheParentArguments()
    {
        var help = Help("shadow", "sub", "c", "d");

        Assert.That(help, Does.Contain("Unrecognized command or argument 'd'."));
        Assert.That(help, Does.Not.Contain("<FIRST>"));
    }

    [Test]
    public void ManuallyBuiltCommands_AreGuardedToo()
    {
        var builder = Tool.CreateBuilder(["manual", "x", "leaf"]);
        var parent = builder.GetCommand("manual");
        parent.Arguments.Add(new Argument<string>("VALUE") { Arity = ArgumentArity.ZeroOrOne });
        var leaf = builder.GetCommand("manual", "leaf");
        leaf.SetAction(_ => { });

        Assert.That(builder.Parse().Errors.Select(e => e.Message),
            Is.EqualTo(new[] { "Unrecognized command or argument 'x'." }));
    }
}
