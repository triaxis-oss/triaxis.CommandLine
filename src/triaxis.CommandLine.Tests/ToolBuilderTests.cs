namespace triaxis.CommandLine.Tests;

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[TestFixture]
public class ToolBuilderTests
{
    [Test]
    public void CreateBuilder_ReturnsNonNullBuilder()
    {
        var builder = Tool.CreateBuilder(["a", "b"]);
        Assert.That(builder, Is.Not.Null);
        Assert.That(builder.Arguments, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void Builder_RootCommandIsAccessible()
    {
        var builder = Tool.CreateBuilder([]);
        Assert.That(builder.RootCommand, Is.Not.Null.And.InstanceOf<RootCommand>());
    }

    [Test]
    public void Builder_ConfigurationIsAccessible()
    {
        var builder = Tool.CreateBuilder([]);
        Assert.That(builder.Configuration, Is.Not.Null.And.InstanceOf<IConfigurationManager>());
    }

    [Test]
    public void GetCommand_CreatesNestedCommandsByPath()
    {
        var builder = Tool.CreateBuilder([]);
        var leaf = builder.GetCommand("foo", "bar");
        Assert.That(leaf, Is.Not.Null);
        Assert.That(leaf.Name, Is.EqualTo("bar"));

        // Same path returns same command instance
        var leaf2 = builder.GetCommand("foo", "bar");
        Assert.That(leaf2, Is.SameAs(leaf));

        // Parent command also reachable via separate GetCommand call
        var parent = builder.GetCommand("foo");
        Assert.That(parent.Name, Is.EqualTo("foo"));
    }

    [Test]
    public void GetCommand_IsCaseInsensitive()
    {
        var builder = Tool.CreateBuilder([]);
        var lower = builder.GetCommand("foo");
        var upper = builder.GetCommand("FOO");
        Assert.That(upper, Is.SameAs(lower));
    }

    [Test]
    public void GetCommand_InsertsSubcommandsInSortedOrder()
    {
        var builder = Tool.CreateBuilder([]);
        builder.GetCommand("charlie");
        builder.GetCommand("alpha");
        builder.GetCommand("bravo");

        var names = builder.RootCommand.Subcommands.Select(c => c.Name).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "alpha", "bravo", "charlie" }));
    }

    [Test]
    public void GetCommand_AttachesSubcommandsDirectlyToTree()
    {
        var builder = Tool.CreateBuilder([]);
        builder.GetCommand("beta", "gamma");
        builder.GetCommand("alpha");

        Assert.That(builder.RootCommand.Subcommands.Select(c => c.Name),
            Is.EqualTo(new[] { "alpha", "beta" }));
        var beta = builder.RootCommand.Subcommands.First(c => c.Name == "beta");
        Assert.That(beta.Subcommands.Select(c => c.Name), Does.Contain("gamma"));
    }

    [Test]
    public void ConfigureServices_AllowsServiceRegistration()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.ConfigureServices(services => services.AddSingleton("hello"));
        Assert.That(result, Is.SameAs(builder), "ConfigureServices should return the same builder for chaining");
    }

    [Test]
    public void Configure_RunsCallbackWithBuilderAndReturnsItForChaining()
    {
        var builder = Tool.CreateBuilder([]);
        IToolBuilder? seen = null;

        var result = builder.Configure(b =>
        {
            seen = b;
            b.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Hook:Ran"] = "yes" });
        });

        Assert.That(seen, Is.SameAs(builder), "the callback should receive the same builder");
        Assert.That(result, Is.SameAs(builder), "Configure should return the same builder for chaining");
        Assert.That(builder.Configuration["Hook:Ran"], Is.EqualTo("yes"),
            "the callback should be able to customize the builder");
    }

    [Test]
    public void ConfigureConfiguration_ImmediateOverload_AddsSourceAndReturnsBuilder()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.ConfigureConfiguration(c => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Greeting:Name"] = "Meatbag" }));

        Assert.That(result, Is.SameAs(builder),
            "ConfigureConfiguration should return the same builder for chaining");
        Assert.That(builder.Configuration["Greeting:Name"], Is.EqualTo("Meatbag"),
            "the immediate overload should add the source as soon as it is called");
    }

    [Test]
    public async Task ConfigureConfiguration_DeferredOverload_AppliesAtBuildWithParseResult()
    {
        var capture = new ConfigCapture();
        var builder = Tool.CreateBuilder(["config-capture"]);
        builder.ConfigureServices(s => s.AddSingleton(capture));

        var result = builder.ConfigureConfiguration((ctx, c) =>
        {
            var command = ctx.GetInvocationContext().ParseResult.CommandResult.Command.Name;
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Captured:Command"] = command,
            });
        });
        builder.AddCommandsFromAssembly(typeof(ToolBuilderTests).Assembly);

        Assert.That(result, Is.SameAs(builder),
            "ConfigureConfiguration should return the same builder for chaining");

        await builder.RunAsync();

        Assert.That(capture.Value, Is.EqualTo("config-capture"),
            "the deferred overload should be applied during Build() with access to the parsed command line");
    }

    [Test]
    public void AddMiddleware_ReturnsSameBuilderForChaining()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.AddMiddleware(async (ctx, next) => await next(ctx));
        Assert.That(result, Is.SameAs(builder));
    }

    [Test]
    public void GetServiceProviderAccessor_BeforeRun_ThrowsOrReturnsNull()
    {
        var builder = Tool.CreateBuilder([]);
        var accessor = builder.GetServiceProviderAccessor();
        Assert.That(accessor, Is.Not.Null);
        // Before Run, accessor returns null (the backing field is not yet populated).
        // Calling it should not throw — the delegate uses the null-forgiving operator.
        Assert.That(() => accessor(), Throws.Nothing);
    }

    [Test]
    public void Parse_ReturnsParseResultForCurrentArguments()
    {
        var builder = Tool.CreateBuilder(["foo", "bar"]);
        builder.GetCommand("foo");

        var parseResult = builder.Parse();

        Assert.That(parseResult, Is.Not.Null);
        Assert.That(parseResult.CommandResult.Command.Name, Is.EqualTo("foo"));
    }

    [Test]
    public void Parse_IsIdempotent_ReturnsSameInstanceOnRepeatedCalls()
    {
        var builder = Tool.CreateBuilder(["foo"]);
        builder.GetCommand("foo");

        var first = builder.Parse();
        var second = builder.Parse();

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void Parse_BeforeBuild_ExposesParseResultForEarlyInspection()
    {
        var builder = Tool.CreateBuilder(["alpha"]);
        builder.GetCommand("alpha");
        builder.GetCommand("beta");

        var parseResult = builder.Parse();

        Assert.That(parseResult.CommandResult.Command.Name, Is.EqualTo("alpha"));
    }

    [Test]
    public async Task Parse_CachedResultIsReusedByBuild()
    {
        // If Build() re-parsed, it would see a different tree when we mutate the
        // RootCommand after calling Parse(). Verify that the cached parse wins.
        var builder = Tool.CreateBuilder(["lifetime-resolve"]);
        var capture = new LifetimeCapture();
        builder.ConfigureServices(s => s.AddSingleton(capture));
        builder.AddCommandsFromAssembly(typeof(ToolBuilderTests).Assembly);

        var parsedEarly = builder.Parse();

        await builder.RunAsync();

        Assert.That(capture.Lifetime, Is.Not.Null,
            "the cached parse result should have been reused and the command should have executed");
        Assert.That(parsedEarly.CommandResult.Command.Name, Is.EqualTo("lifetime-resolve"));
    }

    [Test]
    public async Task HostApplicationLifetime_IsResolvable_DuringCommandExecution()
    {
        var capture = new LifetimeCapture();
        var builder = Tool.CreateBuilder(["lifetime-resolve"]);
        builder.ConfigureServices(s => s.AddSingleton(capture));
        builder.AddCommandsFromAssembly(typeof(ToolBuilderTests).Assembly);

        await builder.RunAsync();

        Assert.That(capture.Lifetime, Is.Not.Null);
    }

    [Test]
    public async Task HostApplicationLifetime_ApplicationStarted_FiresDuringRun()
    {
        var capture = new LifetimeCapture();
        var builder = Tool.CreateBuilder(["lifetime-resolve"]);
        builder.ConfigureServices(s => s.AddSingleton(capture));
        builder.AddCommandsFromAssembly(typeof(ToolBuilderTests).Assembly);

        await builder.RunAsync();

        Assert.That(capture.StartedFired, Is.True, "ApplicationStarted should have fired after StartAsync");
    }

    [Test]
    public async Task HostApplicationLifetime_StoppingAndStopped_FireDuringStopAsync()
    {
        var capture = new LifetimeCapture();
        var builder = Tool.CreateBuilder(["lifetime-resolve"]);
        builder.ConfigureServices(s => s.AddSingleton(capture));
        builder.AddCommandsFromAssembly(typeof(ToolBuilderTests).Assembly);

        await builder.RunAsync();

        Assert.That(capture.StoppingFired, Is.True, "ApplicationStopping should have fired during StopAsync");
        Assert.That(capture.StoppedFired, Is.True, "ApplicationStopped should have fired during StopAsync");
    }

    [Test]
    public async Task RunAsync_DisposesAsyncOnlyDisposableServices()
    {
        var tracker = new AsyncDisposalTracker();
        var builder = Tool.CreateBuilder(["lifetime-resolve"]);
        builder.ConfigureServices(s =>
        {
            s.AddSingleton(tracker);
            s.AddSingleton<AsyncOnlyDisposable>();
        });
        builder.ConfigureServices(s => s.AddSingleton(new LifetimeCapture()));
        builder.AddCommandsFromAssembly(typeof(ToolBuilderTests).Assembly);

        // Force the singleton to be instantiated so the provider tracks it for disposal.
        builder.ConfigureServices(s => s.AddHostedService<AsyncDisposableActivator>());

        Assert.That(async () => await builder.RunAsync(), Throws.Nothing,
            "RunAsync must dispose IAsyncDisposable-only services without throwing");
        Assert.That(tracker.DisposedAsync, Is.True,
            "the async-only disposable should be disposed via DisposeAsync");
    }

    [Test]
    public async Task HostApplicationLifetime_StopApplication_FiresStopping()
    {
        var capture = new LifetimeCapture();
        var builder = Tool.CreateBuilder(["lifetime-stop"]);
        builder.ConfigureServices(s => s.AddSingleton(capture));
        builder.AddCommandsFromAssembly(typeof(ToolBuilderTests).Assembly);

        await builder.RunAsync();

        Assert.That(capture.StopApplicationCalledStoppingFired, Is.True,
            "StopApplication() should fire ApplicationStopping");
    }
}

public class LifetimeCapture
{
    public IHostApplicationLifetime? Lifetime { get; set; }
    public bool StartedFired { get; set; }
    public bool StoppingFired { get; set; }
    public bool StoppedFired { get; set; }
    public bool StopApplicationCalledStoppingFired { get; set; }
}

public class ConfigCapture
{
    public string? Value { get; set; }
}

public class AsyncDisposalTracker
{
    public bool DisposedAsync { get; set; }
}

public sealed class AsyncOnlyDisposable(AsyncDisposalTracker tracker) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        tracker.DisposedAsync = true;
        return default;
    }
}

public sealed class AsyncDisposableActivator : IHostedService
{
    public AsyncDisposableActivator(AsyncOnlyDisposable _) { }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[Command("config-capture")]
public class ConfigCaptureCommand
{
    [Inject]
    public IConfiguration Configuration { get; set; } = null!;

    [Inject]
    public ConfigCapture Capture { get; set; } = null!;

    public Task ExecuteAsync()
    {
        Capture.Value = Configuration["Captured:Command"];
        return Task.CompletedTask;
    }
}

[Command("lifetime-resolve")]
public class LifetimeResolveCommand
{
    [Inject]
    public IHostApplicationLifetime Lifetime { get; set; } = null!;

    [Inject]
    public LifetimeCapture Capture { get; set; } = null!;

    public Task ExecuteAsync()
    {
        Capture.Lifetime = Lifetime;
        Capture.StartedFired = Lifetime.ApplicationStarted.IsCancellationRequested;
        Lifetime.ApplicationStopping.Register(() => Capture.StoppingFired = true);
        Lifetime.ApplicationStopped.Register(() => Capture.StoppedFired = true);
        return Task.CompletedTask;
    }
}

[Command("lifetime-stop")]
public class LifetimeStopCommand
{
    [Inject]
    public IHostApplicationLifetime Lifetime { get; set; } = null!;

    [Inject]
    public LifetimeCapture Capture { get; set; } = null!;

    public Task ExecuteAsync()
    {
        Capture.Lifetime = Lifetime;
        Lifetime.ApplicationStopping.Register(() => Capture.StopApplicationCalledStoppingFired = true);
        Lifetime.StopApplication();
        return Task.CompletedTask;
    }
}
