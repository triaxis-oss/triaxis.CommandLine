namespace triaxis.CommandLine.ToolTests;

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

[TestFixture]
public class YamlConfigurationTests
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
        var dir = Path.Combine(Path.GetTempPath(), "txcl-yaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        return dir;
    }

    private static IConfiguration Build(Action<IConfigurationBuilder> configure)
    {
        var builder = new ConfigurationBuilder();
        configure(builder);
        return builder.Build();
    }

    [Test]
    public void AddYamlStreamReadsWithStandardFlattening()
    {
        const string yaml =
            "Obj:\n" +
            "  K: v\n" +
            "Arr:\n" +
            "  - x\n" +
            "  - y\n" +
            "N: 42\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(yaml));

        var config = Build(b => b.AddYamlStream(stream));

        Assert.That(config["Obj:K"], Is.EqualTo("v"));
        Assert.That(config["Arr:0"], Is.EqualTo("x"));
        Assert.That(config["Arr:1"], Is.EqualTo("y"));
        Assert.That(config["N"], Is.EqualTo("42"));
    }

    [Test]
    public void AddYamlFileReadsAnExistingFile()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "settings.yaml"), "Logging:\n  Level: Debug\n");

        var config = Build(b => b.AddYamlFile(
            new PhysicalFileProvider(dir), "settings.yaml", optional: false, reloadOnChange: false));

        Assert.That(config["Logging:Level"], Is.EqualTo("Debug"));
    }

    [Test]
    public void AddYamlFileLayersOverAnEarlierSource()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "settings.yaml"), "Svc:\n  Token: yaml\n");

        var config = Build(b => b
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Svc:Token"] = "builtin" })
            .AddYamlFile(new PhysicalFileProvider(dir), "settings.yaml", optional: false, reloadOnChange: false));

        Assert.That(config["Svc:Token"], Is.EqualTo("yaml"), "a later read-only YAML layer overrides earlier sources");
    }

    [Test]
    public void OptionalMissingYamlFileIsANoop()
    {
        var config = Build(b => b.AddYamlFile("does-not-exist.yaml", optional: true));

        Assert.That(config["anything"], Is.Null);
    }
}
