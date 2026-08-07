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

    public record Reading(
        [property: ObjectOutput(Format = "F1")] double Celsius,
        [property: ObjectOutput(Format = "yyyy-MM-dd")] DateTime Taken,
        double Raw);

    [Test]
    public async Task AppliesFieldFormatToTextOutput()
    {
        var descriptor = new DefaultObjectDescriptorProvider<Reading>()
            .GetDescriptor(new(0, default, 0));
        var sw = new StringWriter();

        var formatter = await CreateProvider().CreateFormatterAsync<Reading>(descriptor, sw, collection: true);
        await formatter.OutputElementAsync(new Reading(19.876, new DateTime(2026, 8, 6, 12, 30, 0), 19.876));
        await ((IAsyncDisposable)formatter).DisposeAsync();

        var text = sw.ToString();
        Assert.That(text, Does.Contain("19.9"), "Format = F1 should round the value");
        Assert.That(text, Does.Contain("2026-08-06"));
        Assert.That(text, Does.Not.Contain("12:30"), "the date format should drop the time");
        Assert.That(text, Does.Contain("19.876"), "a field without a format keeps the general one");
    }
}
