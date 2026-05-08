namespace triaxis.CommandLine.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// End-to-end coverage for per-command static <c>Configure</c> methods. The hook fires
/// only for the command that the parsed command line actually selects; sibling commands'
/// hooks stay dormant.
/// </summary>
[TestFixture]
public class ConfigureMethodIntegrationTests
{
    [SetUp]
    public void Reset()
    {
        ConfigureProbeCommand.ConfigureCount = 0;
        ConfigureProbeCommand.LastBuilder = null;
        ConfigureProbeCommand.LastServices = null;
        ConfigureProbeCommand.ResolvedMarker = null;
        ConfigureSiblingCommand.ConfigureCount = 0;
        ConfigureSiblingCommand.Ran = false;
    }

    [Test]
    public async Task Configure_RunsOnce_WhenItsOwnCommandIsInvoked()
    {
        var builder = Tool.CreateBuilder(["configure-probe"]);
        builder.AddCommandsFromAssembly(typeof(ConfigureMethodIntegrationTests).Assembly);

        var exit = await builder.RunAsync();
        Assert.That(exit, Is.EqualTo(0));

        Assert.That(ConfigureProbeCommand.ConfigureCount, Is.EqualTo(1),
            "Configure must run exactly once when its command is invoked");
        Assert.That(ConfigureProbeCommand.LastBuilder, Is.SameAs(builder));
        Assert.That(ConfigureProbeCommand.LastServices, Is.Not.Null);
        Assert.That(ConfigureProbeCommand.ResolvedMarker, Is.EqualTo("configure-marker"),
            "the singleton registered by Configure must be resolvable from the host");
    }

    [Test]
    public async Task Configure_DoesNotRun_WhenADifferentCommandIsInvoked()
    {
        var builder = Tool.CreateBuilder(["configure-sibling"]);
        builder.AddCommandsFromAssembly(typeof(ConfigureMethodIntegrationTests).Assembly);

        var exit = await builder.RunAsync();
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(ConfigureSiblingCommand.Ran, Is.True);

        Assert.That(ConfigureProbeCommand.ConfigureCount, Is.EqualTo(0),
            "Configure on a sibling command must stay dormant when its own command isn't invoked");
        Assert.That(ConfigureProbeCommand.ResolvedMarker, Is.Null);
    }

    [Test]
    public void Configure_DoesNotRun_ForBuiltInActions()
    {
        // --help routes through System.CommandLine's HelpAction, which is not the
        // command's own action — so the per-command Configure hook must not fire.
        // We Build (not Run) to avoid actually rendering help to the console; that's
        // enough since Configure is invoked from inside Build().
        var builder = Tool.CreateBuilder(["configure-probe", "--help"]);
        builder.AddCommandsFromAssembly(typeof(ConfigureMethodIntegrationTests).Assembly);

        using var host = ((IHostBuilder)builder).Build();

        Assert.That(ConfigureProbeCommand.ConfigureCount, Is.EqualTo(0),
            "Built-in actions like --help must not trigger Configure");
    }

    [Test]
    public void Configure_DoesNotRun_OnAddCommandsFromAssemblyAlone()
    {
        // Adding the assembly's commands must not be enough to fire Configure —
        // the hook is bound to action invocation, not registration.
        var builder = Tool.CreateBuilder(["configure-sibling"]);
        builder.AddCommandsFromAssembly(typeof(ConfigureMethodIntegrationTests).Assembly);

        Assert.That(ConfigureProbeCommand.ConfigureCount, Is.EqualTo(0));
        Assert.That(ConfigureSiblingCommand.ConfigureCount, Is.EqualTo(0));
    }
}

[Command("configure-probe")]
public class ConfigureProbeCommand
{
    public static int ConfigureCount;
    public static IToolBuilder? LastBuilder;
    public static IServiceCollection? LastServices;
    public static string? ResolvedMarker;

    [Inject]
    public IServiceProvider Provider { get; set; } = null!;

    public static void Configure(IToolBuilder builder, IServiceCollection services)
    {
        ConfigureCount++;
        LastBuilder = builder;
        LastServices = services;
        services.AddSingleton(new ConfigureMarker("configure-marker"));
    }

    public void Execute()
    {
        ResolvedMarker = Provider.GetService<ConfigureMarker>()?.Value;
    }
}

[Command("configure-sibling")]
public class ConfigureSiblingCommand
{
    public static int ConfigureCount;
    public static bool Ran;

    public static void Configure(IToolBuilder builder)
    {
        ConfigureCount++;
    }

    public void Execute()
    {
        Ran = true;
    }
}

/// <summary>
/// Integration coverage for the instance form of <c>Configure</c>: the source generator
/// constructs the command and binds <c>[Argument]</c>/<c>[Option]</c> values before
/// invoking the user's instance method, so it can register services keyed off the
/// parsed values. The configure-phase instance is reused by <c>Execute</c> so any
/// state set in <c>Configure</c> carries through.
/// </summary>
[TestFixture]
public class InstanceConfigureIntegrationTests
{
    [SetUp]
    public void Reset()
    {
        InstanceConfigureCommand.LastObservedTarget = null;
        InstanceConfigureCommand.LastObservedFlag = null;
        InstanceConfigureCommand.LastConfigureMarker = null;
        InstanceConfigureCommand.LastExecuteMarker = null;
        InstanceConfigureCommand.SameInstance = null;
    }

    [Test]
    public async Task InstanceConfigure_SeesBoundOptions()
    {
        var builder = Tool.CreateBuilder(["instance-configure", "alpha", "--flag"]);
        builder.AddCommandsFromAssembly(typeof(InstanceConfigureIntegrationTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(InstanceConfigureCommand.LastObservedTarget, Is.EqualTo("alpha"),
            "instance Configure must observe the bound [Argument] value");
        Assert.That(InstanceConfigureCommand.LastObservedFlag, Is.True,
            "instance Configure must observe the bound [Option] value");
    }

    [Test]
    public async Task InstanceConfigure_StateFlowsToExecute_OnSameInstance()
    {
        var builder = Tool.CreateBuilder(["instance-configure", "beta"]);
        builder.AddCommandsFromAssembly(typeof(InstanceConfigureIntegrationTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(InstanceConfigureCommand.LastConfigureMarker, Is.EqualTo("configure-saw-beta"));
        Assert.That(InstanceConfigureCommand.LastExecuteMarker, Is.EqualTo("configure-saw-beta"),
            "Execute must run on the same instance that Configure mutated");
        Assert.That(InstanceConfigureCommand.SameInstance, Is.True);
    }

    [Test]
    public async Task InstanceConfigure_InjectsAreAvailableAtExecute_NotAtConfigure()
    {
        var builder = Tool.CreateBuilder(["instance-configure", "gamma"]);
        builder.AddCommandsFromAssembly(typeof(InstanceConfigureIntegrationTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(InstanceConfigureCommand.ProviderObservedAtConfigure, Is.False,
            "Configure runs before the service provider exists, so [Inject] members are still null");
        Assert.That(InstanceConfigureCommand.ProviderObservedAtExecute, Is.True,
            "by Execute time, InjectServices has populated the [Inject] members");
    }
}

[Command("instance-configure")]
public class InstanceConfigureCommand
{
    public static string? LastObservedTarget;
    public static bool? LastObservedFlag;
    public static string? LastConfigureMarker;
    public static string? LastExecuteMarker;
    public static bool? SameInstance;
    public static bool ProviderObservedAtConfigure;
    public static bool ProviderObservedAtExecute;

    [Argument("target")]
    public string Target { get; set; } = "";

    [Option("--flag")]
    public bool Flag { get; set; }

    // Non-required [Inject] — populated by InjectServices after Configure runs.
    [Inject]
    public InstanceConfigureProbe Probe { get; set; } = null!;

    private string? _marker;
    private InstanceConfigureCommand? _selfAtConfigure;

    public void Configure(IServiceCollection services)
    {
        LastObservedTarget = Target;
        LastObservedFlag = Flag;
        _marker = $"configure-saw-{Target}";
        LastConfigureMarker = _marker;
        // Probe is null at Configure time — InjectServices runs later, after the
        // host is built. Use ReferenceEquals against null to dodge any null-warning
        // from the [Inject]'s null-forgiven default.
        ProviderObservedAtConfigure = !ReferenceEquals(Probe, null);
        _selfAtConfigure = this;
        services.AddSingleton<InstanceConfigureProbe>();
        services.AddSingleton(new InstanceConfigureMarker(Target));
    }

    public void Execute()
    {
        LastExecuteMarker = _marker;
        SameInstance = ReferenceEquals(_selfAtConfigure, this);
        ProviderObservedAtExecute = !ReferenceEquals(Probe, null);
    }
}

public sealed record InstanceConfigureMarker(string Value);

public sealed class InstanceConfigureProbe { }

public sealed record ConfigureMarker(string Value);
