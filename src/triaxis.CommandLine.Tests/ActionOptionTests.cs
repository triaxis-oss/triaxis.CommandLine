namespace triaxis.CommandLine.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class ActionOptionTrace
{
    public string? Method { get; set; }
    public string? Suffix { get; set; }
    public CancellationToken LastToken { get; set; }
    public IToolBuilder? Builder { get; set; }
}

[Command("ao-regular")]
public class ActionOptionRegularCommand
{
    [Inject]
    public ActionOptionTrace Trace { get; set; } = null!;

    [Option("--suffix")]
    public string Suffix { get; set; } = "";

    public Task ExecuteAsync()
    {
        Trace.Method = "primary";
        Trace.Suffix = Suffix;
        return Task.CompletedTask;
    }

    [ActionOption("--list", "-l", Description = "List items")]
    public Task ListAsync()
    {
        Trace.Method = "list";
        Trace.Suffix = Suffix;
        return Task.CompletedTask;
    }

    // No explicit name — derived from method (with `Async` suffix stripped).
    [ActionOption]
    public Task<int> RestoreAsync(CancellationToken ct)
    {
        Trace.Method = "restore";
        Trace.LastToken = ct;
        return Task.FromResult(7);
    }

    [ActionOption("--sync-void")]
    public void SyncVoid()
    {
        Trace.Method = "sync-void";
        Trace.Suffix = Suffix;
    }

    [ActionOption("--sync-int")]
    public int SyncInt()
    {
        Trace.Method = "sync-int";
        Trace.Suffix = Suffix;
        return 5;
    }
}

[Command("ao-standalone")]
public class ActionOptionStandaloneCommand
{
    public static ActionOptionTrace Trace { get; set; } = new();

    [Option("--suffix")]
    public string Suffix { get; set; } = "";

    public Task MainAsync()
    {
        Trace.Method = "primary";
        Trace.Suffix = Suffix;
        return Task.CompletedTask;
    }

    [ActionOption("--init")]
    public Task<int> InitAsync(IToolBuilder builder, CancellationToken ct)
    {
        Trace.Method = "init";
        Trace.Suffix = Suffix;
        Trace.Builder = builder;
        Trace.LastToken = ct;
        return Task.FromResult(3);
    }

    [ActionOption("--ping")]
    public Task PingAsync()
    {
        Trace.Method = "ping";
        Trace.Suffix = Suffix;
        return Task.CompletedTask;
    }

    [ActionOption("--sync-tag")]
    public void SyncTag()
    {
        Trace.Method = "sync-tag";
        Trace.Suffix = Suffix;
    }
}

[TestFixture]
public class ActionOptionTests
{
    [SetUp]
    public void Reset()
    {
        ActionOptionStandaloneCommand.Trace = new ActionOptionTrace();
    }

    [Test]
    public async Task Regular_Primary_RunsWhenNoActionOptionGiven()
    {
        var trace = new ActionOptionTrace();
        var builder = Tool.CreateBuilder(["ao-regular", "--suffix", "x"]);
        builder.ConfigureServices(s => s.AddSingleton(trace));
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(trace.Method, Is.EqualTo("primary"));
        Assert.That(trace.Suffix, Is.EqualTo("x"));
    }

    [Test]
    public async Task Regular_ActionOption_DispatchesToAlternateMethod()
    {
        var trace = new ActionOptionTrace();
        var builder = Tool.CreateBuilder(["ao-regular", "--list", "--suffix", "y"]);
        builder.ConfigureServices(s => s.AddSingleton(trace));
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(trace.Method, Is.EqualTo("list"));
        // The alternate method still sees the command's regular options bound onto the instance.
        Assert.That(trace.Suffix, Is.EqualTo("y"));
    }

    [Test]
    public async Task Regular_ActionOption_AcceptsCancellationTokenAndReturnsExitCode()
    {
        var trace = new ActionOptionTrace();
        // No explicit name — defaults to "--restore" (Async stripped, kebab-cased)
        // following the same convention as auto-named [Option] members.
        var builder = Tool.CreateBuilder(["ao-regular", "--restore"]);
        builder.ConfigureServices(s => s.AddSingleton(trace));
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(trace.Method, Is.EqualTo("restore"));
        Assert.That(exit, Is.EqualTo(7));
        Assert.That(trace.LastToken.CanBeCanceled, Is.True);
    }

    [Test]
    public async Task Regular_ActionOption_AliasIsRecognized()
    {
        var trace = new ActionOptionTrace();
        var builder = Tool.CreateBuilder(["ao-regular", "-l"]);
        builder.ConfigureServices(s => s.AddSingleton(trace));
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        await builder.RunAsync();

        Assert.That(trace.Method, Is.EqualTo("list"));
    }

    [Test]
    public async Task Standalone_ActionOption_WithToolBuilder_ShortCircuitsToStandalone()
    {
        var builder = Tool.CreateBuilder(["ao-standalone", "--init", "--suffix", "z"]);
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        // Action options whose method takes IToolBuilder cause the option's action to
        // implement IStandaloneAction — the host short-circuits to StandaloneHost.
        using (var host = ((IHostBuilder)builder).Build())
        {
            Assert.That(host, Is.InstanceOf<StandaloneHost>());
        }

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(3));
        Assert.That(ActionOptionStandaloneCommand.Trace.Method, Is.EqualTo("init"));
        Assert.That(ActionOptionStandaloneCommand.Trace.Suffix, Is.EqualTo("z"));
        Assert.That(ActionOptionStandaloneCommand.Trace.Builder, Is.SameAs(builder));
    }

    [Test]
    public async Task Standalone_ActionOption_WithoutToolBuilder_StillRunsStandalone()
    {
        var builder = Tool.CreateBuilder(["ao-standalone", "--ping"]);
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(ActionOptionStandaloneCommand.Trace.Method, Is.EqualTo("ping"));
    }

    [Test]
    public async Task Regular_ActionOption_SyncVoid_IsDispatched()
    {
        var trace = new ActionOptionTrace();
        var builder = Tool.CreateBuilder(["ao-regular", "--sync-void", "--suffix", "v"]);
        builder.ConfigureServices(s => s.AddSingleton(trace));
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(trace.Method, Is.EqualTo("sync-void"));
        Assert.That(trace.Suffix, Is.EqualTo("v"));
    }

    [Test]
    public async Task Regular_ActionOption_SyncInt_ReturnsExitCode()
    {
        var trace = new ActionOptionTrace();
        var builder = Tool.CreateBuilder(["ao-regular", "--sync-int"]);
        builder.ConfigureServices(s => s.AddSingleton(trace));
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(trace.Method, Is.EqualTo("sync-int"));
        Assert.That(exit, Is.EqualTo(5));
    }

    [Test]
    public async Task Standalone_ActionOption_SyncVoid_IsDispatched()
    {
        var builder = Tool.CreateBuilder(["ao-standalone", "--sync-tag", "--suffix", "s"]);
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(ActionOptionStandaloneCommand.Trace.Method, Is.EqualTo("sync-tag"));
        Assert.That(ActionOptionStandaloneCommand.Trace.Suffix, Is.EqualTo("s"));
    }

    [Test]
    public async Task Standalone_Primary_StillRunsWhenNoActionOption()
    {
        var builder = Tool.CreateBuilder(["ao-standalone", "--suffix", "main"]);
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        await builder.RunAsync();

        Assert.That(ActionOptionStandaloneCommand.Trace.Method, Is.EqualTo("primary"));
        Assert.That(ActionOptionStandaloneCommand.Trace.Suffix, Is.EqualTo("main"));
    }
}

// A regular DI-routed command with an [ActionOption] that takes IToolBuilder. The
// builder-taking action option opts itself into the standalone path independently of
// the primary command's kind.
[Command("ao-mixed")]
public class ActionOptionMixedCommand
{
    public static IToolBuilder? ReceivedBuilder { get; set; }
    public static string? ReceivedSuffix { get; set; }
    public static string? Method { get; set; }

    [Inject]
    public ActionOptionTrace Trace { get; set; } = null!;

    [Option("--suffix")]
    public string Suffix { get; set; } = "";

    public Task ExecuteAsync()
    {
        Method = "primary";
        Trace.Method = "primary";
        Trace.Suffix = Suffix;
        return Task.CompletedTask;
    }

    [ActionOption("--migrate")]
    public Task<int> MigrateAsync(IToolBuilder builder, CancellationToken ct)
    {
        Method = "migrate";
        ReceivedBuilder = builder;
        ReceivedSuffix = Suffix;
        return Task.FromResult(11);
    }
}

[TestFixture]
public class ActionOptionMixedKindTests
{
    [SetUp]
    public void Reset()
    {
        ActionOptionMixedCommand.ReceivedBuilder = null;
        ActionOptionMixedCommand.ReceivedSuffix = null;
        ActionOptionMixedCommand.Method = null;
    }

    [Test]
    public async Task DiCommand_BuilderActionOption_RunsInStandalonePath()
    {
        var builder = Tool.CreateBuilder(["ao-mixed", "--migrate", "--suffix", "v2"]);
        // Register the trace service; the primary path needs it (via [Inject]) but
        // the action option path skips DI altogether — it must not depend on it.
        builder.ConfigureServices(s => s.AddSingleton(new ActionOptionTrace()));
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        // Builder-taking action option => its action implements IStandaloneAction =>
        // ToolBuilder.Build() short-circuits to StandaloneHost even though the command's
        // primary is ExecuteAsync.
        using (var host = ((IHostBuilder)builder).Build())
        {
            Assert.That(host, Is.InstanceOf<StandaloneHost>(),
                "A builder-taking [ActionOption] should route through StandaloneHost regardless of the primary's kind.");
        }

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(11));
        Assert.That(ActionOptionMixedCommand.Method, Is.EqualTo("migrate"));
        Assert.That(ActionOptionMixedCommand.ReceivedBuilder, Is.SameAs(builder));
        // Regular options are still bound onto the instance for the action option.
        Assert.That(ActionOptionMixedCommand.ReceivedSuffix, Is.EqualTo("v2"));
    }

    [Test]
    public async Task DiCommand_PrimaryPath_StillUsesDi()
    {
        var trace = new ActionOptionTrace();
        var builder = Tool.CreateBuilder(["ao-mixed", "--suffix", "default"]);
        builder.ConfigureServices(s => s.AddSingleton(trace));
        builder.AddCommandsFromAssembly(typeof(ActionOptionTests).Assembly);

        // No builder-taking option matched => the command's own (DI) action is selected
        // and Build() returns ToolHost.
        using (var host = ((IHostBuilder)builder).Build())
        {
            Assert.That(host, Is.InstanceOf<ToolHost>());
        }

        await builder.RunAsync();

        Assert.That(ActionOptionMixedCommand.Method, Is.EqualTo("primary"));
        Assert.That(trace.Method, Is.EqualTo("primary"));
        Assert.That(trace.Suffix, Is.EqualTo("default"));
    }
}

[TestFixture]
public class ActionOptionAttributeTests
{
    [Test]
    public void ActionOptionAttribute_AliasesConstructor()
    {
        var attr = new ActionOptionAttribute("--list", "-l", "-ll");
        Assert.That(attr.Name, Is.EqualTo("--list"));
        Assert.That(attr.Aliases, Is.EqualTo(new[] { "-l", "-ll" }));
    }

    [Test]
    public void ActionOptionAttribute_DefaultsToNullName()
    {
        var attr = new ActionOptionAttribute();
        Assert.That(attr.Name, Is.Null);
        Assert.That(attr.Aliases, Is.Null);
    }
}
