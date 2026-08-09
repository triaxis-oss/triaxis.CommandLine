namespace triaxis.CommandLine.Tests;

using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

public class ShadowState
{
    public string? First { get; set; }
    public string? Second { get; set; }
    public string? Third { get; set; }
    public string? Ran { get; set; }
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

    public void Execute()
    {
        State.Ran = "parent";
        State.First = First;
        State.Second = Second;
    }
}

[Command("shadow", "sub")]
public class ShadowChildCommand
{
    [Inject]
    public ShadowState State { get; set; } = null!;

    [Argument]
    public string? Third { get; set; }

    public void Execute()
    {
        State.Ran = "child";
        State.Third = Third;
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
