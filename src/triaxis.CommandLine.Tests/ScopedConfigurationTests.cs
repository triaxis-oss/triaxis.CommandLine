namespace triaxis.CommandLine.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

[TestFixture]
public class ScopedConfigurationTests
{
    private static IConfigurationRoot Build(Action<ScopedConfigurationBuilder> configure)
    {
        var scoped = new ScopedConfigurationBuilder();
        configure(scoped);
        return new ConfigurationBuilder().Add(scoped.BuildSource()).Build();
    }

    private static Action<IConfigurationBuilder> InMemory(params (string Key, string Value)[] pairs)
        => c => c.AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value));

    [Test]
    public void MoreSpecificScopePrimaryWins()
    {
        var config = Build(b => b
            .Add(ConfigurationScope.Builtin, InMemory(("X", "builtin")))
            .Add(ConfigurationScope.Machine, InMemory(("X", "machine")))
            .Add(ConfigurationScope.User, InMemory(("X", "user")))
            .Add(ConfigurationScope.EnvironmentVariables, InMemory(("X", "env"))));

        Assert.That(config["X"], Is.EqualTo("env"),
            "the most specific scope that defines a key wins");
    }

    [Test]
    public void SubtreeOverlaysPrimaryWithinTheSameScope()
    {
        var config = Build(b => b
            .Add(ConfigurationScope.Builtin, InMemory(
                ("X", "primary"),
                ("Active:X", "subtree")))
            .Remap("Active"));

        Assert.That(config["X"], Is.EqualTo("subtree"),
            "within one scope, a remapped subtree value overlays that scope's own primary value");
    }

    [Test]
    public void LessSpecificScopeSubtreeDoesNotOverrideMoreSpecificScopePrimary()
    {
        // The core invariant: a Builtin subtree overlay must not clobber an explicit
        // User-scope primary value.
        var config = Build(b => b
            .Add(ConfigurationScope.Builtin, InMemory(
                ("X", "builtin-primary"),
                ("Active:X", "builtin-subtree")))
            .Add(ConfigurationScope.User, InMemory(("X", "user-primary")))
            .Remap("Active"));

        Assert.That(config["X"], Is.EqualTo("user-primary"),
            "a less specific scope's remapped subtree must not override a more specific scope's primary value");
    }

    [Test]
    public void LessSpecificScopeSubtreeStillFillsKeysNoMoreSpecificScopeDefines()
    {
        var config = Build(b => b
            .Add(ConfigurationScope.Builtin, InMemory(("Active:OnlyHere", "builtin-subtree")))
            .Add(ConfigurationScope.User, InMemory(("X", "user-primary")))
            .Remap("Active"));

        Assert.That(config["OnlyHere"], Is.EqualTo("builtin-subtree"),
            "the subtree still supplies keys no more specific scope defines");
        Assert.That(config["X"], Is.EqualTo("user-primary"));
    }

    [Test]
    public void MoreSpecificScopeSubtreeBeatsLessSpecificScopePrimary()
    {
        // The rule is about scope specificity, not "primary always beats subtree".
        var config = Build(b => b
            .Add(ConfigurationScope.Builtin, InMemory(("X", "builtin-primary")))
            .Add(ConfigurationScope.User, InMemory(("Active:X", "user-subtree")))
            .Remap("Active"));

        Assert.That(config["X"], Is.EqualTo("user-subtree"),
            "a more specific scope's subtree beats a less specific scope's primary");
    }

    [Test]
    public void ArbitraryPathToPathRemap()
    {
        var config = Build(b => b
            .Add(ConfigurationScope.Builtin, InMemory(
                ("Profiles:CI:Logging:Level", "Debug"),
                ("Profiles:CI:Logging:Path", "/var/log")))
            .Remap("Profiles:CI", "Settings"));

        Assert.That(config["Settings:Logging:Level"], Is.EqualTo("Debug"));
        Assert.That(config["Settings:Logging:Path"], Is.EqualTo("/var/log"));
    }

    [Test]
    public void ReloadInAScopeIsFoldedBackInAndPropagated()
    {
        var src = new ReloadableSource(new() { ["X"] = "before" });
        var scoped = new ScopedConfigurationBuilder();
        scoped.Add(ConfigurationScope.Builtin, c => c.Add(src));
        var config = new ConfigurationBuilder().Add(scoped.BuildSource()).Build();

        Assert.That(config["X"], Is.EqualTo("before"));

        var changed = 0;
        ChangeToken.OnChange(config.GetReloadToken, () => changed++);

        src.Provider!.Update("X", "after");

        Assert.That(config["X"], Is.EqualTo("after"),
            "a reload inside a scope is folded back into the scoped configuration");
        Assert.That(changed, Is.GreaterThan(0),
            "the reload is propagated to consumers of the scoped configuration");
    }

    [Test]
    public void RemapIsAppliedIndependentlyPerScope()
    {
        // Each scope overlays its own subtree; neither leaks into the other's primary
        // resolution beyond the scope precedence rule.
        var config = Build(b => b
            .Add(ConfigurationScope.Builtin, InMemory(
                ("Active:A", "builtin-A"),
                ("Active:B", "builtin-B")))
            .Add(ConfigurationScope.User, InMemory(("Active:A", "user-A")))
            .Remap("Active"));

        Assert.That(config["A"], Is.EqualTo("user-A"), "User subtree overlays Builtin subtree for A");
        Assert.That(config["B"], Is.EqualTo("builtin-B"), "Builtin subtree still supplies B");
    }

    private sealed class ReloadableSource(Dictionary<string, string?> data) : IConfigurationSource
    {
        public ReloadableProvider? Provider { get; private set; }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
            => Provider = new ReloadableProvider(data);
    }

    private sealed class ReloadableProvider(Dictionary<string, string?> data) : ConfigurationProvider
    {
        public override void Load()
            => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

        public void Update(string key, string? value)
        {
            data[key] = value;
            Load();
            OnReload();
        }
    }
}
