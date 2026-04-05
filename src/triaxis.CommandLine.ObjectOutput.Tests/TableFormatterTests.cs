namespace triaxis.CommandLine.ObjectOutput.Tests;

using Microsoft.Extensions.Options;
using triaxis.CommandLine.ObjectOutput.Formatters;

[TestFixture]
public class TableFormatterTests
{
    public record Fruit(string Name, int Count);

    private static TableObjectFormatterProvider CreateProvider(bool wide = false)
        => new(Options.Create(new TableOutputOptions { Wide = wide }));

    [Test]
    public async Task RendersHeaderRowWithColumnNames()
    {
        var descriptor = new DefaultObjectDescriptorProvider<Fruit>().GetDescriptor(new("", 0));
        var sw = new StringWriter();
        var provider = CreateProvider();

        var formatter = await provider.CreateFormatterAsync<Fruit>(descriptor, sw, collection: true);
        await formatter.OutputElementAsync(new Fruit("apple", 3));
        await ((IAsyncDisposable)formatter).DisposeAsync();

        var text = sw.ToString();
        Assert.That(text, Does.Contain("NAME"));
        Assert.That(text, Does.Contain("COUNT"));
        Assert.That(text, Does.Contain("apple"));
        Assert.That(text, Does.Contain("3"));
    }

    [Test]
    public async Task RendersMultipleRows()
    {
        var descriptor = new DefaultObjectDescriptorProvider<Fruit>().GetDescriptor(new("", 0));
        var sw = new StringWriter();
        var provider = CreateProvider();

        var formatter = await provider.CreateFormatterAsync<Fruit>(descriptor, sw, collection: true);
        await formatter.OutputElementAsync(new Fruit("apple", 1));
        await formatter.OutputElementAsync(new Fruit("banana", 2));
        await ((IAsyncDisposable)formatter).DisposeAsync();

        var text = sw.ToString();
        Assert.That(text, Does.Contain("apple"));
        Assert.That(text, Does.Contain("banana"));
    }
}
