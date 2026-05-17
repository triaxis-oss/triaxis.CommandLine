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

    private static (Func<Environment.SpecialFolder, string> Resolve, string Root) TempFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "txcl-overrides-" + Guid.NewGuid().ToString("N"));
        string Sub(string name)
        {
            var p = Path.Combine(root, name);
            Directory.CreateDirectory(p);
            return p;
        }

        string machine = Sub("machine"), roaming = Sub("roaming"), local = Sub("local");
        Func<Environment.SpecialFolder, string> resolve = f => f switch
        {
            Environment.SpecialFolder.CommonApplicationData => machine,
            Environment.SpecialFolder.ApplicationData => roaming,
            Environment.SpecialFolder.LocalApplicationData => local,
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };
        return (resolve, root);
    }

    private static readonly Action<IConfigurationBuilder, string, string> ReadValueIntoX =
        (cfg, dir, file) =>
        {
            // Mirrors a real provider added with optional:true — an absent file
            // contributes nothing, but the source is still registered.
            var path = Path.Combine(dir, file);
            if (File.Exists(path))
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["X"] = File.ReadAllText(path) });
            }
        };

    [Test]
    public void AddOverridesLayersUserOverMachineAndLocalLast()
    {
        var (resolve, root) = TempFolders();
        try
        {
            File.WriteAllText(Path.Combine(root, "machine", "o.cfg"), "machine");
            File.WriteAllText(Path.Combine(root, "roaming", "o.cfg"), "user-roaming");
            File.WriteAllText(Path.Combine(root, "local", "o.cfg"), "user-local");

            var config = Build(b => b.AddOverrides("o.cfg", ReadValueIntoX, resolve));

            Assert.That(config["X"], Is.EqualTo("user-local"),
                "User scope beats Machine, and within User the LocalApplicationData probe is added last");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AddOverridesFallsBackToMachineWhenNoUserFile()
    {
        var (resolve, root) = TempFolders();
        try
        {
            File.WriteAllText(Path.Combine(root, "machine", "o.cfg"), "machine");

            var config = Build(b => b.AddOverrides("o.cfg", ReadValueIntoX, resolve));

            Assert.That(config["X"], Is.EqualTo("machine"),
                "the Machine probe supplies the value when no per-user file exists");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AddOverridesSkipsAbsentFiles()
    {
        var (resolve, root) = TempFolders();
        try
        {
            var config = Build(b => b.AddOverrides("o.cfg", ReadValueIntoX, resolve));

            Assert.That(config["X"], Is.Null, "no file in any probed folder contributes nothing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AddOverridesRegistersEachFolderEvenWhenFilesAbsent()
    {
        // The file is registered unconditionally so a watcher exists for a file
        // written later; otherwise a long-running process never sees it.
        var (resolve, root) = TempFolders();
        try
        {
            var calls = new List<string>();
            _ = Build(b => b.AddOverrides("o.cfg", (_, dir, file) => calls.Add(Path.Combine(dir, file)), resolve));

            Assert.That(calls, Has.Count.EqualTo(3),
                "addFile must run for the Machine probe and both User probes regardless of file presence");
            Assert.That(calls, Has.All.EndsWith("o.cfg"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
