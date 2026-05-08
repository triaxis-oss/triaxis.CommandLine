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

[Command("init-props")]
public class InitPropsCommand
{
    [Argument("path", Required = false)]
    public string Path { get; init; } = "default-path";

    [Option("--flag")]
    public bool Flag { get; init; }

    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = $"{Path}:{Flag}";
        return Task.CompletedTask;
    }
}

[Command("nullable-opt")]
public class NullableOptCommand
{
    [Option("--tag", Required = false)]
    public string? Tag { get; set; }

    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = Tag ?? "(null)";
        return Task.CompletedTask;
    }
}

[Command("nullable-init-opt")]
public class NullableInitOptCommand
{
    [Option("--count", Required = false)]
    public int? Count { get; init; }

    [Option("--label", Required = false)]
    public string? Label { get; init; }

    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = $"{Count?.ToString() ?? "(null)"}:{Label ?? "(null)"}";
        return Task.CompletedTask;
    }
}

[Command("cascade-init-props")]
public class CascadeInitPropsCommand
{
    // Mirrors the real-world trigger for the fix: a write-only `init` property
    // that cascades into several auto-implemented init-only options.
    [Option("--apply")] public bool Apply { get; init; } = false;
    [Option("--db-drop")] public bool DropDb { get; init; } = false;
    [Option("--db-skip-backup")] public bool SkipDbBackup { get; init; } = false;
    [Option("--delete-data")] public bool DeleteData { get; init; } = false;

    // No getter, no backing field — previously broke the generator because it
    // tried to emit `ref bool __access_Purge(...)` against a non-existent
    // <Purge>k__BackingField.
    [Option("--dev-purge")]
    public bool Purge { init => DropDb = SkipDbBackup = DeleteData = value; }

    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = $"{Apply}:{DropDb}:{SkipDbBackup}:{DeleteData}";
        return Task.CompletedTask;
    }
}

[Command("custom-init-props")]
public class CustomInitPropsCommand
{
    // A property with a custom getter/init (no compiler-synthesized backing field)
    // would normally break the source generator's backing-field accessor path.
    private string _path = "default-custom";
    private bool? _flag;

    [Argument("path", Required = false)]
    public string Path
    {
        get => _path;
        init => _path = value ?? "default-custom";
    }

    [Option("--flag", Required = false)]
    public bool? Flag
    {
        get => _flag;
        init => _flag = value;
    }

    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = $"{Path}:{Flag?.ToString() ?? "(null)"}";
        return Task.CompletedTask;
    }
}

[Command(Description = "Root-level command with no path")]
public class RootLevelCommand
{
    [Option("--name")]
    public string Name { get; set; } = "root";

    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = Name;
        return Task.CompletedTask;
    }
}

[Command("decimal-arg")]
public class DecimalArgCommand
{
    [Argument("value")]
    public double Value { get; set; }

    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

[Command("required-inject")]
public class RequiredInjectCommand
{
    [Inject]
    public required EchoState State { get; init; }

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = "required-inject";
        return Task.CompletedTask;
    }
}

public abstract class BaseEchoCommandWithCt
{
    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        State.WasRun = true;
        State.Name = $"base-ct:{cancellationToken.CanBeCanceled}";
        return Task.CompletedTask;
    }
}

[Command("inherit-ct")]
public class InheritCtCommand : BaseEchoCommandWithCt
{
}

public abstract class BaseEchoCommandSync
{
    [Inject]
    public EchoState State { get; set; } = null!;

    public void Execute()
    {
        State.WasRun = true;
        State.Name = "base-sync";
    }
}

[Command("inherit-sync")]
public class InheritSyncCommand : BaseEchoCommandSync
{
}

public abstract class BaseWithAsync
{
    [Inject]
    public EchoState State { get; set; } = null!;

    public Task ExecuteAsync()
    {
        State.WasRun = true;
        State.Name = "base-async";
        return Task.CompletedTask;
    }
}

[Command("derived-sync-wins")]
public class DerivedSyncOverridesBaseAsync : BaseWithAsync
{
    public void Execute()
    {
        State.WasRun = true;
        State.Name = "derived-sync";
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
    public async Task Run_RequiredInject_IsResolvedThroughInjectServices()
    {
        // The binder writes `default!` for required [Inject] members in the object
        // initializer just to satisfy the C# `required` modifier; InjectServices is
        // what actually resolves the value. This test guards that wiring — if the
        // accessor for an init-only required member regresses, the command would
        // execute with State == null and NRE before WasRun gets set.
        var state = new EchoState();
        var builder = CreateBuilder(["required-inject"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("required-inject"));
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
    public async Task Run_InitOnlyProperties_BoundWhenSpecified()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["init-props", "/tmp", "--flag"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("/tmp:True"));
    }

    [Test]
    public async Task Run_InitOnlyProperties_UseDefaultsWhenNotSpecified()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["init-props"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("default-path:False"));
    }

    [Test]
    public async Task Run_NullableOption_IsNullWhenNotSpecified()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["nullable-opt"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.Name, Is.EqualTo("(null)"));
    }

    [Test]
    public async Task Run_NullableOption_IsSetWhenSpecified()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["nullable-opt", "--tag", "hello"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.Name, Is.EqualTo("hello"));
    }

    [Test]
    public async Task Run_NullableInitOnlyOption_IsNullWhenNotSpecified()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["nullable-init-opt"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("(null):(null)"));
    }

    [Test]
    public async Task Run_NullableInitOnlyOption_IsSetWhenSpecified()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["nullable-init-opt", "--count", "42", "--label", "hi"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("42:hi"));
    }

    [Test]
    public async Task Run_CustomInitOnlyProperties_BoundWhenSpecified()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["custom-init-props", "/custom", "--flag"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("/custom:True"));
    }

    [Test]
    public async Task Run_CustomInitOnlyProperties_UseDefaultsWhenNotSpecified()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["custom-init-props"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        // Nullable bool flag must stay null — proves the setter isn't being
        // called with `default(bool?)` when the user didn't pass --flag.
        Assert.That(state.Name, Is.EqualTo("default-custom:(null)"));
    }

    [Test]
    public async Task Run_CascadeInitOnlyProperty_DefaultsAllFalse()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["cascade-init-props"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.Name, Is.EqualTo("False:False:False:False"));
    }

    [Test]
    public async Task Run_CascadeInitOnlyProperty_CascadesThroughWriteOnlyInit()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["cascade-init-props", "--dev-purge"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        // --dev-purge sets DropDb=SkipDbBackup=DeleteData=true via a write-only
        // init accessor (no getter, no backing field). Apply stays false.
        Assert.That(state.Name, Is.EqualTo("False:True:True:True"));
    }

    [Test]
    public async Task Run_CustomInitOnlyNullableBool_OverridesNullDefault()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["custom-init-props", "--flag", "false"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        // Explicit --flag false must overwrite the null default, proving the
        // init setter is actually invoked on the custom-impl property.
        Assert.That(state.Name, Is.EqualTo("default-custom:False"));
    }

    [Test]
    public async Task Run_DecimalArgument_ParsedWithInvariantCulture()
    {
        var state = new EchoState();
        var savedCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // Use a culture with comma as decimal separator
            var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NumberDecimalSeparator = ",";
            System.Globalization.CultureInfo.CurrentCulture = culture;
            var builder = CreateBuilder(["decimal-arg", "3.14"], state);
            var exitCode = await builder.RunAsync();

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(state.WasRun, Is.True);
            Assert.That(state.Name, Is.EqualTo("3.14"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = savedCulture;
        }
    }

    [Test]
    public async Task Run_RootLevelCommand_ExecutesWithNoPath()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["--name", "hello"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("hello"));
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

    [Test]
    public async Task Run_InheritedExecuteAsync_WithCancellationToken_IsDetected()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["inherit-ct"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("base-ct:True"));
    }

    [Test]
    public async Task Run_InheritedSyncExecute_IsDetected()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["inherit-sync"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("base-sync"));
    }

    [Test]
    public async Task Run_DerivedSyncExecute_WinsOverBaseExecuteAsync()
    {
        var state = new EchoState();
        var builder = CreateBuilder(["derived-sync-wins"], state);

        var exitCode = await builder.RunAsync();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(state.WasRun, Is.True);
        Assert.That(state.Name, Is.EqualTo("derived-sync"));
    }
}
