namespace triaxis.CommandLine.ObjectOutput.Tests;

using Microsoft.Extensions.DependencyInjection;

public record Widget(string Name, int Qty);

[Command("widgets")]
public class WidgetsCommand
{
    public IEnumerable<Widget> Execute()
        => [new Widget("alpha", 1), new Widget("beta", 2)];
}

/// <summary>
/// Tests that wiring ObjectOutput through the builder actually produces output
/// for commands that return a result. Uses a custom output stream provider to
/// capture formatter output without touching the real console.
/// </summary>
[TestFixture]
public class UseObjectOutputPipelineTests
{
    private sealed class CapturingStreamProvider : IOutputStreamProvider
    {
        public StringWriter Writer { get; } = new();
        public TextWriter GetOutputStream() => Writer;
    }

    private static async Task<string> RunAsync(params string[] args)
    {
        var capture = new CapturingStreamProvider();
        var builder = Tool.CreateBuilder(args);
        builder.UseObjectOutput();
        builder.ConfigureServices(s => s.AddSingleton<IOutputStreamProvider>(capture));
        builder.AddCommandsFromAssembly(typeof(UseObjectOutputPipelineTests).Assembly);

        var exit = await builder.RunAsync();
        Assert.That(exit, Is.EqualTo(0));
        return capture.Writer.ToString();
    }

    [Test]
    public async Task TableOutput_ShowsHeadersAndRows()
    {
        var text = await RunAsync("widgets");
        Assert.That(text, Does.Contain("NAME"));
        Assert.That(text, Does.Contain("QTY"));
        Assert.That(text, Does.Contain("alpha"));
        Assert.That(text, Does.Contain("beta"));
    }

    [Test]
    public async Task JsonOutput_FormatIsValidJsonArray()
    {
        var text = (await RunAsync("widgets", "--output", "Json")).Trim();
        Assert.That(text.StartsWith("["), Is.True);
        Assert.That(text.EndsWith("]"), Is.True);
        Assert.That(text, Does.Contain("alpha"));
        Assert.That(text, Does.Contain("beta"));
    }

    [Test]
    public async Task YamlOutput_UsesYamlSyntax()
    {
        var text = await RunAsync("widgets", "--output", "Yaml");
        Assert.That(text, Does.Contain("- Name: alpha"));
        Assert.That(text, Does.Contain("Qty: 2"));
    }

    [Test]
    public async Task NoneOutput_DiscardsButStillRunsCommand()
    {
        var text = await RunAsync("widgets", "--output", "None");
        Assert.That(text, Is.Empty);
    }

    [Test]
    public void UseObjectOutput_AddsOutputOptionToRoot()
    {
        var builder = Tool.CreateBuilder([]).UseObjectOutput();
        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        Assert.That(names, Does.Contain("--output"));
    }
}
