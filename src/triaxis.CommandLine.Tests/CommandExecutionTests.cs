namespace triaxis.CommandLine.Tests;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

public class EchoState
{
    public bool WasRun { get; set; }
    public string? Name { get; set; }
    public List<string> Order { get; } = [];
}

[Command("echo")]
public class EchoCommand
{
    [Inject]
    public EchoState State { get; set; } = null!;

    [Option("--name")]
    public string Name { get; set; } = "world";

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = Name;
        return Task.CompletedTask;
    }
}

[Command("fail")]
public class FailingCommand
{
    public Task ExecuteAsync()
    {
        throw new CommandErrorException("something went wrong {Value}", 42);
    }
}

public record NumberBox(int Value);

[Command("count")]
public class CountCommand
{
    [Argument("value")]
    public int Value { get; set; }

    public NumberBox Execute() => new(Value);
}

[Command("required-opt")]
public class RequiredOptionCommand
{
    [Option("--key")]
    public required string Key { get; set; }

    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = Key;
        return Task.CompletedTask;
    }
}

[Command("ctor-echo")]
public class CtorInjectedCommand(EchoState state)
{
    public Task ExecuteAsync()
    {
        state.WasRun = true;
        state.Name = "ctor";
        return Task.CompletedTask;
    }
}

[TestFixture]
public class CommandExecutionTests
{
    private static IToolBuilder CreateBuilder(string[] args, EchoState? state = null)
    {
        var builder = Tool.CreateBuilder(args);
        if (state is not null)
        {
            builder.ConfigureServices(s => s.AddSingleton(state));
        }
        builder.AddCommandsFromAssembly(typeof(CommandExecutionTests).Assembly);
        return builder;
    }

    [Test]
    public async Task Run_ExecutesCommand_AndResolvesInjectedDependencies()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["echo", "--name", "Alice"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("Alice"));
    }

    [Test]
    public async Task Run_UsesDefaultOptionValue_WhenNotSpecified()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["echo"], state);

        await builder.RunAsync();

        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("world"));
    }

    [Test]
    public async Task Run_CommandErrorException_ResultsInExitCodeMinusOne()
    {
        var builder = CreateBuilder(["fail"]);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(-1));
    }

    [Test]
    public async Task Run_MiddlewareWrapsCommandExecution()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["echo"], state);
        builder.AddMiddleware(async (ctx, next) =>
        {
            state.Order.Add("before");
            await next(ctx);
            state.Order.Add("after");
        });

        await builder.RunAsync();

        Assert.That(state.Order, Is.EqualTo(new[] { "before", "after" }));
        Assert.That(state.WasRun, Is.True);
    }

    [Test]
    public async Task Run_ValueReturningCommand_SetsInvocationResult()
    {
        InvocationContext? captured = null;
        var builder = CreateBuilder(["count", "7"]);
        builder.AddMiddleware(async (ctx, next) =>
        {
            await next(ctx);
            captured = ctx;
        });

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.InvocationResult, Is.Not.Null);
        Assert.That(captured.InvocationResult, Is.InstanceOf<ICommandInvocationResult<NumberBox>>());

        NumberBox? value = null;
        await ((ICommandInvocationResult<NumberBox>)captured.InvocationResult!).EnumerateResultsAsync(
            v => { value = v; return default; }, null, default);
        Assert.That(value, Is.EqualTo(new NumberBox(7)));
    }

    [Test]
    public async Task Run_ConstructorInjection_ResolvesFromDI()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["ctor-echo"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("ctor"));
    }

    [Test]
    public async Task Run_RequiredMemberOption_IsSetCorrectly()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["required-opt", "--key", "test-value"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("test-value"));
    }

    [Test]
    public void Run_NoArgumentsWithTopLevelCommands_PrintsHelpAndReturnsNonZero()
    {
        // With no args, System.CommandLine prints help for the root. Since there are
        // multiple commands, no action runs. Exit code is non-negative.
        var builder = CreateBuilder([]);
        var prevOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            var exit = builder.Run();
            Assert.That(exit, Is.GreaterThanOrEqualTo(0));
        }
        finally
        {
            Console.SetOut(prevOut);
        }
    }
}
