namespace triaxis.CommandLine.ObjectOutput.Tests;

using triaxis.CommandLine.ObjectOutput.Formatters;

[TestFixture]
public class YamlFormatterTests
{
    public record Item(string Name, int Count);

    [Test]
    public async Task OutputsObject_AsYaml()
    {
        var provider = new DefaultObjectDescriptorProvider<Item>();
        var descriptor = provider.GetDescriptor(new Item("apple", 3));
        var sw = new StringWriter();
        var fmt = new YamlObjectFormatterProvider([]);

        var formatter = await fmt.CreateFormatterAsync<Item>(descriptor, sw, collection: false);
        await formatter.OutputElementAsync(new Item("apple", 3));

        var yaml = sw.ToString();
        Assert.That(yaml, Does.Contain("Name: apple"));
        Assert.That(yaml, Does.Contain("Count: 3"));
    }

    [Test]
    public async Task OutputsCollection_AsYamlSequence()
    {
        var provider = new DefaultObjectDescriptorProvider<Item>();
        var descriptor = provider.GetDescriptor(new Item("", 0));
        var sw = new StringWriter();
        var fmt = new YamlObjectFormatterProvider([]);

        var formatter = await fmt.CreateFormatterAsync<Item>(descriptor, sw, collection: true);
        await formatter.OutputElementAsync(new Item("one", 1));

        var yaml = sw.ToString();
        Assert.That(yaml, Does.Contain("- Name: one"));
        Assert.That(yaml, Does.Contain("Count: 1"));
    }
}
