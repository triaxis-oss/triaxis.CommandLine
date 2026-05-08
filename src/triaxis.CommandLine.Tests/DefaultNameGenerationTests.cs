namespace triaxis.CommandLine.Tests;

[Command("kebab-defaults")]
public class KebabDefaultsCommand
{
    public static string? LastValue;

    [Option]
    public string MyOption { get; set; } = "";

    [Option]
    public string ParseHTMLDocument { get; set; } = "";

    [Option]
    public bool X { get; set; }

    [Argument]
    public string MyArg { get; set; } = "";

    public Task ExecuteAsync()
    {
        LastValue = $"{MyOption}|{ParseHTMLDocument}|{X}|{MyArg}";
        return Task.CompletedTask;
    }
}

[TestFixture]
public class DefaultNameGenerationTests
{
    [SetUp]
    public void Reset() => KebabDefaultsCommand.LastValue = null;

    private static IToolBuilder CreateBuilder(string[] args)
    {
        var builder = Tool.CreateBuilder(args);
        builder.AddCommandsFromAssembly(typeof(DefaultNameGenerationTests).Assembly);
        return builder;
    }

    [Test]
    public void OptionsUseKebabCase_ArgumentsUseScreamingKebab()
    {
        var builder = CreateBuilder([]);
        var cmd = builder.GetCommand("kebab-defaults");

        var optionNames = cmd.Options.Select(o => o.Name).ToList();
        Assert.That(optionNames, Does.Contain("--my-option"));
        Assert.That(optionNames, Does.Contain("--parse-html-document"));
        Assert.That(optionNames, Does.Contain("-x"));

        var argumentNames = cmd.Arguments.Select(a => a.Name).ToList();
        Assert.That(argumentNames, Does.Contain("MY-ARG"));
    }

    [Test]
    public async Task DefaultNamedOptionsAndArguments_BindFromCommandLine()
    {
        var builder = CreateBuilder(["kebab-defaults", "argval", "--my-option", "a", "--parse-html-document", "b", "-x"]);
        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(KebabDefaultsCommand.LastValue, Is.EqualTo("a|b|True|argval"));
    }
}
