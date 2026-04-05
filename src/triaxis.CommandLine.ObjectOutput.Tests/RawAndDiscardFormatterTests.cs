namespace triaxis.CommandLine.ObjectOutput.Tests;

using triaxis.CommandLine.ObjectOutput.Formatters;

[TestFixture]
public class RawAndDiscardFormatterTests
{
    [Test]
    public async Task RawFormatter_WritesToStringPerElement()
    {
        var sw = new StringWriter();
        var provider = new RawObjectFormatterProvider();
        var descriptor = new DefaultObjectDescriptorProvider<string>().GetDescriptor("x");

        var formatter = await provider.CreateFormatterAsync<string>(descriptor, sw, collection: true);
        await formatter.OutputElementAsync("hello");
        await formatter.OutputElementAsync("world");

        var lines = sw.ToString().Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Is.EqualTo(new[] { "hello", "world" }));
    }

    [Test]
    public async Task RawFormatter_NullValueWritesEmptyLine()
    {
        var sw = new StringWriter();
        var provider = new RawObjectFormatterProvider();
        var descriptor = new DefaultObjectDescriptorProvider<string>().GetDescriptor("x");

        var formatter = await provider.CreateFormatterAsync<string>(descriptor, sw, collection: false);
        await formatter.OutputElementAsync(null!);

        Assert.That(sw.ToString(), Is.EqualTo(Environment.NewLine));
    }

    [Test]
    public async Task DiscardFormatter_WritesNothing()
    {
        var sw = new StringWriter();
        var provider = new DiscardObjectFormatterProvider();
        var descriptor = new DefaultObjectDescriptorProvider<string>().GetDescriptor("x");

        var formatter = await provider.CreateFormatterAsync<string>(descriptor, sw, collection: true);
        await formatter.OutputElementAsync("hello");
        await formatter.OutputElementAsync("world");

        Assert.That(sw.ToString(), Is.Empty);
    }
}
