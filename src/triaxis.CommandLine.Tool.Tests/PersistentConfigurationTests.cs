namespace triaxis.CommandLine.ToolTests;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

[TestFixture]
public class PersistentConfigurationTests
{
    private readonly List<string> _temp = [];

    [TearDown]
    public void Cleanup()
    {
        foreach (var dir in _temp)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        _temp.Clear();
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "txcl-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        return dir;
    }

    private static IConfiguration BuildScoped(Action<ScopedConfigurationBuilder> configure)
    {
        var host = new HostBuilder().UseScopedConfiguration(configure).Build();
        return host.Services.GetRequiredService<IConfiguration>();
    }

    private static void AddUserFile(ScopedConfigurationBuilder s, string dir, string file, bool json)
        => s.Add(ConfigurationScope.User, cfg =>
        {
            var fp = new PhysicalFileProvider(dir);
            if (json)
            {
                cfg.AddPersistentJsonFile(fp, file, optional: true, reloadOnChange: false);
            }
            else
            {
                cfg.AddPersistentYamlFile(fp, file, optional: true, reloadOnChange: false);
            }
        });

    [Test]
    public void JsonUpdateWritesNestedFileAndLiveConfigReflectsItImmediately()
    {
        var dir = TempDir();
        var config = BuildScoped(s =>
        {
            s.Add(ConfigurationScope.Builtin, c => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Svc:Token"] = "builtin" }));
            AddUserFile(s, dir, "config.json", json: true);
        });

        var changed = 0;
        ChangeToken.OnChange(config.GetReloadToken, () => changed++);

        config.Update(ConfigurationScope.User, cp => cp.Set("Svc:Token", "written"));

        Assert.That(config["Svc:Token"], Is.EqualTo("written"),
            "the User-scope write overlays the Builtin value and Save propagates it live");
        Assert.That(changed, Is.GreaterThan(0), "Save raises the reload token");

        var onDisk = File.ReadAllText(Path.Combine(dir, "config.json"));
        Assert.That(onDisk, Does.Contain("\"Svc\""), "value is written back nested, not flat");
        Assert.That(onDisk, Does.Contain("\"Token\": \"written\""));
    }

    [Test]
    public void JsonWriteIsDurableAcrossAFreshConfiguration()
    {
        var dir = TempDir();
        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp =>
            {
                cp.Set("A:B:C", "deep");
                cp.Set("Top", "v");
            });

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));

        Assert.That(reread["A:B:C"], Is.EqualTo("deep"));
        Assert.That(reread["Top"], Is.EqualTo("v"));
    }

    [Test]
    public void YamlUpdateWritesAndLiveConfigReflectsItAndIsDurable()
    {
        var dir = TempDir();
        var config = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));

        config.Update(ConfigurationScope.User, cp => cp.Set("Logging:Level", "Debug"));

        Assert.That(config["Logging:Level"], Is.EqualTo("Debug"));

        var onDisk = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        Assert.That(onDisk, Does.Contain("Logging:"));
        Assert.That(onDisk, Does.Contain("Level: Debug"));

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["Logging:Level"], Is.EqualTo("Debug"));
    }

    [Test]
    public void ScopeTargetingWritesOnlyTheRequestedLayer()
    {
        var userDir = TempDir();
        var machineDir = TempDir();
        var config = BuildScoped(s =>
        {
            s.Add(ConfigurationScope.User, c => c.AddPersistentJsonFile(
                new PhysicalFileProvider(userDir), "u.json", optional: true, reloadOnChange: false));
            s.Add(ConfigurationScope.Machine, c => c.AddPersistentJsonFile(
                new PhysicalFileProvider(machineDir), "m.json", optional: true, reloadOnChange: false));
        });

        config.Update(ConfigurationScope.User, cp => cp.Set("K", "user"));

        Assert.That(File.Exists(Path.Combine(userDir, "u.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(machineDir, "m.json")), Is.False,
            "a User-targeted write must not touch the Machine layer");
    }

    [Test]
    public void ExistingJsonFileIsReadWithStandardFlattening()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"),
            """{ "Obj": { "K": "v" }, "Arr": [ "x", "y" ], "N": 42, "B": true }""");

        var config = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));

        Assert.That(config["Obj:K"], Is.EqualTo("v"));
        Assert.That(config["Arr:0"], Is.EqualTo("x"));
        Assert.That(config["Arr:1"], Is.EqualTo("y"));
        Assert.That(config["N"], Is.EqualTo("42"));
        Assert.That(config["B"], Is.EqualTo("true"));
    }

    [Test]
    public void PersistentYamlFileResolvesAnAbsolutePathWithoutAnExplicitProvider()
    {
        var dir = TempDir();
        var absolute = Path.Combine(dir, "abs.yaml");
        File.WriteAllText(absolute, "Svc:\n  Token: rooted\n");

        var builder = new ConfigurationBuilder();
        builder.AddPersistentYamlFile(absolute, optional: true, reloadOnChange: false);
        var config = builder.Build();

        Assert.That(config["Svc:Token"], Is.EqualTo("rooted"),
            "a rooted --config path with no explicit provider must still load");
    }

    [Test]
    public void SaveThrowsWhenTheProviderHasNoPhysicalPath()
    {
        var config = BuildScoped(s => s.Add(ConfigurationScope.User, c =>
            c.AddPersistentJsonFile(new InMemoryFileProvider(), "x.json", optional: true, reloadOnChange: false)));

        Assert.That(() => config.Update(ConfigurationScope.User, cp => cp.Set("a", "b")),
            Throws.InvalidOperationException);
    }

    // ---- preservation: editing an existing file -----------------------------

    [Test]
    public void JsonValueEditChangesOnlyThatTokenAndKeepsCommentsAndLayout()
    {
        var dir = TempDir();
        string original =
            "{\n" +
            "  // user settings\n" +
            "  \"Svc\": {\n" +
            "    \"Token\": \"old\",   // rotate me\n" +
            "    \"Url\": \"https://example\"\n" +
            "  }\n" +
            "}\n";
        File.WriteAllText(Path.Combine(dir, "config.json"), original);

        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp => cp.Set("Svc:Token", "new"));

        string updated = File.ReadAllText(Path.Combine(dir, "config.json"));
        Assert.That(updated, Is.EqualTo(original.Replace("\"old\"", "\"new\"")),
            "only the changed value token is rewritten; comments, the sibling key, and all whitespace are byte-identical");
    }

    [Test]
    public void JsonInsertNewKeyKeepsCommentsAndIsReadableAndValid()
    {
        var dir = TempDir();
        string original = "{\n  // header\n  \"A\": \"1\"\n}\n";
        File.WriteAllText(Path.Combine(dir, "config.json"), original);

        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp => cp.Set("B", "2"));

        string updated = File.ReadAllText(Path.Combine(dir, "config.json"));
        Assert.That(updated, Does.Contain("// header"), "the comment survives an insert");
        Assert.That(updated, Does.Contain("\"A\": \"1\""), "the existing member is untouched");
        Assert.That(() => JsonDocument.Parse(updated, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }),
            Throws.Nothing, "the result is still valid JSON");

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));
        Assert.That(reread["A"], Is.EqualTo("1"));
        Assert.That(reread["B"], Is.EqualTo("2"));
    }

    [Test]
    public void JsonInsertCreatesMissingNestedParentsAndPreservesTheComment()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"), "{\n  // keep\n  \"X\": \"1\"\n}\n");

        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp => cp.Set("A:B:C", "deep"));

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));
        Assert.That(reread["A:B:C"], Is.EqualTo("deep"));
        Assert.That(reread["X"], Is.EqualTo("1"));
        Assert.That(File.ReadAllText(Path.Combine(dir, "config.json")), Does.Contain("// keep"));
    }

    [Test]
    public void JsonRemoveKeyDropsItButKeepsTheRest()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"),
            "{\n  // c\n  \"A\": \"1\",\n  \"B\": \"2\"\n}\n");

        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp => cp.Set("A", null));

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));
        Assert.That(reread["A"], Is.Null);
        Assert.That(reread["B"], Is.EqualTo("2"));
        Assert.That(File.ReadAllText(Path.Combine(dir, "config.json")), Does.Contain("// c"));
    }

    [Test]
    public void JsonArrayIsRewrittenToAnObjectWhenItGainsANonPositionalKey()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"),
            "{\n  // arr\n  \"Arr\": [\n    \"x\",\n    \"y\"\n  ]\n}\n");

        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp => cp.Set("Arr:foo", "z"));

        string updated = File.ReadAllText(Path.Combine(dir, "config.json"));
        Assert.That(updated, Does.Contain("// arr"), "the comment survives the array→object rewrite");
        Assert.That(updated, Does.Not.Contain("["), "the array was converted in place");

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));
        Assert.That(reread["Arr:0"], Is.EqualTo("x"));
        Assert.That(reread["Arr:1"], Is.EqualTo("y"));
        Assert.That(reread["Arr:foo"], Is.EqualTo("z"));
    }

    [Test]
    public void YamlValueEditKeepsCommentsAndLayout()
    {
        var dir = TempDir();
        string original =
            "# top comment\n" +
            "Svc:\n" +
            "  Token: old   # rotate me\n" +
            "  Url: https://example\n";
        File.WriteAllText(Path.Combine(dir, "config.yaml"), original);

        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("Svc:Token", "new"));

        string updated = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        Assert.That(updated, Does.Contain("# top comment"));
        Assert.That(updated, Does.Contain("# rotate me"));
        Assert.That(updated, Does.Contain("Url: https://example"));
        Assert.That(updated, Does.Contain("Token: new"));
        Assert.That(updated, Does.Not.Contain("Token: old"));
    }

    [Test]
    public void YamlInsertNewKeyKeepsCommentsAndIsReadable()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "# header\nA: '1'\n");

        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("B", "2"));

        string updated = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        // The new key starts its own line; the leading comment is not duplicated and
        // A's value is not mangled into a trailing comment.
        Assert.That(updated, Is.EqualTo("# header\nA: '1'\nB: '2'\n"));

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["A"], Is.EqualTo("1"));
        Assert.That(reread["B"], Is.EqualTo("2"));
    }

    [Test]
    public void YamlInsertSiblingDoesNotCorruptThePrecedingValue()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "First:\n  A: 1\n");

        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("First:B", "2"));

        string updated = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        // Regression: the inserted sibling used to copy the first member's lead, which
        // carries the parent ':' — turning "A: 1" into "A: 1:" and breaking the file.
        Assert.That(updated, Is.EqualTo("First:\n  A: 1\n  B: '2'\n"));

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["First:A"], Is.EqualTo("1"));
        Assert.That(reread["First:B"], Is.EqualTo("2"));
    }

    [Test]
    public void YamlSetNewNestedValueCreatesParentsInPlaceAndKeepsSiblings()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "A:\n  B: 1\n");

        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("A:C:D", "2"));

        // The missing C/D branch is created in place; the existing A:B is untouched
        // (it used to be dropped when the insert fell back to a fresh, dirty-only write).
        string updated = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        Assert.That(updated, Is.EqualTo("A:\n  B: 1\n  C:\n    D: '2'\n"));

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["A:B"], Is.EqualTo("1"));
        Assert.That(reread["A:C:D"], Is.EqualTo("2"));
    }

    [Test]
    public void YamlSetNewTopLevelNestedKeyKeepsExisting()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "Existing: 1\n");

        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("New:Sub", "v"));

        string updated = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        Assert.That(updated, Is.EqualTo("Existing: 1\nNew:\n  Sub: v\n"));
    }

    [Test]
    public void YamlScalarReplacedByMappingWhenAChildKeyIsSet()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "A: 1\n");

        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("A:B", "v"));

        string updated = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        Assert.That(updated, Is.EqualTo("A:\n  B: v\n"));
    }

    [Test]
    public void YamlMultipleEditsInOneSavePreserveUntouchedKeysAndComments()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "# c\nSvc:\n  Token: old\n  Url: u\n");

        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp =>
            {
                cp.Set("Svc:Token", "new");
                cp.Set("Svc:Retries", "3");
                cp.Set("Other:X", "y");
            });

        string updated = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        Assert.That(updated, Is.EqualTo(
            "# c\nSvc:\n  Token: new\n  Url: u\n  Retries: '3'\nOther:\n  X: y\n"));
    }

    [Test]
    public void YamlUneditableFileFallsBackWithoutDroppingData()
    {
        var dir = TempDir();
        // Flow style is rejected by the in-place editor; the fallback must still write
        // back every key, not just the one that changed.
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "{ A: 1, B: 2 }\n");

        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("C", "3"));

        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["A"], Is.EqualTo("1"));
        Assert.That(reread["B"], Is.EqualTo("2"));
        Assert.That(reread["C"], Is.EqualTo("3"));
    }

    [Test]
    public void YamlRemoveFirstMemberOfMapping()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "A:\n  B: 1\n  C: 2\n");
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("A:B", null));
        // Removing the first key must keep A's ':' separator, not collapse it to "A".
        Assert.That(File.ReadAllText(Path.Combine(dir, "config.yaml")), Is.EqualTo("A:\n  C: 2\n"));
        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["A:C"], Is.EqualTo("2"));
        Assert.That(reread["A:B"], Is.Null);
    }

    [Test]
    public void YamlIncrementalDeepNestingAcrossSaves()
    {
        var dir = TempDir();
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("A:B:C", "1"));
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("A:B:D", "2"));
        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["A:B:C"], Is.EqualTo("1"));
        Assert.That(reread["A:B:D"], Is.EqualTo("2"));
    }

    [Test]
    public void YamlValueWithSpecialCharactersRoundTrips()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "K: x\n");
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("Url", "http://a:b/c #1"));
        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["Url"], Is.EqualTo("http://a:b/c #1"));
        Assert.That(reread["K"], Is.EqualTo("x"));
    }

    [Test]
    public void YamlSetKeyUnderSequenceConvertsAndKeepsContext()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "Top: t\nArr:\n  - x\n  - y\n");
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("Arr:k", "v"));
        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["Top"], Is.EqualTo("t"));
        Assert.That(reread["Arr:k"], Is.EqualTo("v"));
    }

    [Test]
    public void YamlEmptyFileSetNestedThenAddSibling()
    {
        var dir = TempDir();
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("A:B", "1"));
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("A:C", "2"));
        var reread = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(reread["A:B"], Is.EqualTo("1"));
        Assert.That(reread["A:C"], Is.EqualTo("2"));
    }

    // ---- adversarial: JSON ---------------------------------------------------

    [Test]
    public void JsonNonObjectRootFallbackKeepsData()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"), "[ \"a\", \"b\" ]\n");
        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp => cp.Set("K", "v"));
        var r = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));
        Assert.That(r["0"], Is.EqualTo("a"));
        Assert.That(r["1"], Is.EqualTo("b"));
        Assert.That(r["K"], Is.EqualTo("v"));
    }

    [Test]
    public void JsonRemoveFirstMemberKeepsSiblings()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"), "{\n  \"A\": 1,\n  \"B\": 2\n}\n");
        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp => cp.Set("A", null));
        var r = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));
        Assert.That(r["A"], Is.Null);
        Assert.That(r["B"], Is.EqualTo("2"));
    }

    [Test]
    public void JsonMultiEditAndNestedCreateKeepUntouched()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.json"),
            "{\n  // hdr\n  \"Svc\": { \"Token\": \"old\", \"Url\": \"u\" }\n}\n");
        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp =>
            {
                cp.Set("Svc:Token", "new");
                cp.Set("Svc:Retries", "3");
                cp.Set("Other:X", "y");
            });
        string updated = File.ReadAllText(Path.Combine(dir, "config.json"));
        // The comment is kept exactly once (not duplicated onto the new top-level key),
        // and the freshly created nested object is indented relative to its parent.
        Assert.That(updated, Is.EqualTo(
            "{\n  // hdr\n  \"Svc\": { \"Token\": \"new\", \"Url\": \"u\", \"Retries\": \"3\" },\n  \"Other\": {\n    \"X\": \"y\"\n  }\n}\n"));
        var r = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));
        Assert.That(r["Svc:Token"], Is.EqualTo("new"));
        Assert.That(r["Svc:Url"], Is.EqualTo("u"));
        Assert.That(r["Svc:Retries"], Is.EqualTo("3"));
        Assert.That(r["Other:X"], Is.EqualTo("y"));
    }

    [Test]
    public void JsonIncrementalDeepNestingAcrossSaves()
    {
        var dir = TempDir();
        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp => cp.Set("A:B:C", "1"));
        BuildScoped(s => AddUserFile(s, dir, "config.json", json: true))
            .Update(ConfigurationScope.User, cp => cp.Set("A:B:D", "2"));
        var r = BuildScoped(s => AddUserFile(s, dir, "config.json", json: true));
        Assert.That(r["A:B:C"], Is.EqualTo("1"));
        Assert.That(r["A:B:D"], Is.EqualTo("2"));
    }

    // ---- adversarial: YAML edge inputs --------------------------------------

    [Test]
    public void YamlRootSequenceFallbackKeepsData()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "- a\n- b\n");
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("K", "v"));
        var r = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(r["0"], Is.EqualTo("a"));
        Assert.That(r["1"], Is.EqualTo("b"));
        Assert.That(r["K"], Is.EqualTo("v"));
    }

    [Test]
    public void YamlAnchorFileFallbackKeepsData()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "A: &x hello\nB: *x\n");
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("C", "3"));
        var r = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(r["A"], Is.EqualTo("hello"));
        Assert.That(r["B"], Is.EqualTo("hello"));
        Assert.That(r["C"], Is.EqualTo("3"));
    }

    [Test]
    public void YamlCrlfFilePreservesData()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "Svc:\r\n  Token: old\r\n  Url: u\r\n");
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("Svc:Token", "new"));
        var r = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(r["Svc:Token"], Is.EqualTo("new"));
        Assert.That(r["Svc:Url"], Is.EqualTo("u"));
    }

    [Test]
    public void YamlQuotedKeyAndBlockScalarSiblingPreserved()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "'my key': v\nText: |\n  line1\n  line2\n");
        BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false))
            .Update(ConfigurationScope.User, cp => cp.Set("New", "n"));
        var r = BuildScoped(s => AddUserFile(s, dir, "config.yaml", json: false));
        Assert.That(r["my key"], Is.EqualTo("v"));
        Assert.That(r["Text"], Is.EqualTo("line1\nline2\n"));
        Assert.That(r["New"], Is.EqualTo("n"));
    }

    // A file provider with no physical backing — Save has nowhere to persist to.
    private sealed class InMemoryFileProvider : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath) => new Info();
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

        private sealed class Info : IFileInfo
        {
            public bool Exists => true;
            public long Length => 2;
            public string? PhysicalPath => null;
            public string Name => "x.json";
            public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
            public bool IsDirectory => false;
            public Stream CreateReadStream() => new MemoryStream("{}"u8.ToArray());
        }
    }
}
