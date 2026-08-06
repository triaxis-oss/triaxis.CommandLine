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
        var fmt = new YamlObjectFormatterProvider();

        var formatter = await fmt.CreateFormatterAsync<Item>(descriptor, sw, collection: false);
        await formatter.OutputElementAsync(new Item("apple", 3));

        Assert.That(sw.ToString(), Is.EqualTo("Name: apple\nCount: 3\n"));
    }

    [Test]
    public async Task OutputsCollection_AsYamlSequence()
    {
        var provider = new DefaultObjectDescriptorProvider<Item>();
        var descriptor = provider.GetDescriptor(new Item("", 0));
        var sw = new StringWriter();
        var fmt = new YamlObjectFormatterProvider();

        var formatter = await fmt.CreateFormatterAsync<Item>(descriptor, sw, collection: true);
        await formatter.OutputElementAsync(new Item("one", 1));
        await formatter.OutputElementAsync(new Item("two", 2));

        Assert.That(sw.ToString(), Is.EqualTo(
            "- Name: one\n" +
            "  Count: 1\n" +
            "- Name: two\n" +
            "  Count: 2\n"));
    }

    [Test]
    public async Task OutputIsPlatformIndependent()
    {
        var provider = new DefaultObjectDescriptorProvider<Item>();
        var descriptor = provider.GetDescriptor(new Item("", 0));
        var sw = new StringWriter { NewLine = "\r\n" };
        var fmt = new YamlObjectFormatterProvider();

        var formatter = await fmt.CreateFormatterAsync<Item>(descriptor, sw, collection: false);
        await formatter.OutputElementAsync(new Item("apple", 3));

        Assert.That(sw.ToString(), Does.Not.Contain("\r"));
    }
}
