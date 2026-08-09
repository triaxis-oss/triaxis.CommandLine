namespace triaxis.CommandLine.ObjectOutput.Tests;

using Microsoft.Extensions.Options;
using triaxis.CommandLine.ObjectOutput.Formatters;

/// <summary>
/// <see cref="ObjectFieldVisibility.Hidden"/> is not a filter — the field must never reach
/// <c>IObjectDescriptor.Fields</c>, because the data formats do not filter at all and a
/// custom formatter would not know to.
/// </summary>
[TestFixture]
public class HiddenVisibilityTests
{
    public record Secretive(
        string Shown,
        [property: ObjectOutput(ObjectFieldVisibility.Internal)] string Internal,
        [property: ObjectOutput(ObjectFieldVisibility.Hidden)] string Secret);

    public record Wrapper(string Name, Secretive Child);

    public record AllHidden([property: ObjectOutput(ObjectFieldVisibility.Hidden)] string Only);

    private static IObjectDescriptor Describe<T>(T value)
        => new DefaultObjectDescriptorProvider<T>().GetDescriptor(value);

    private static readonly Secretive Sample = new("shown", "internal", "s3cr3t");

    [Test]
    public void HiddenFieldIsAbsentFromTheDescriptor()
    {
        Assert.That(Describe(Sample).Fields.Select(f => f.Name), Is.EqualTo(new[] { "Shown", "Internal" }));
    }

    [Test]
    public void HiddenFieldIsAbsentFromYaml()
    {
        var sw = new StringWriter();
        new YamlWriter(sw, RuntimeObjectDescriptor.For).WriteElement(Describe(Sample), Sample, sequenceItem: false);

        Assert.That(sw.ToString(), Is.EqualTo("Shown: shown\nInternal: internal\n"));
    }

    [Test]
    public void HiddenFieldIsAbsentFromJson()
    {
        var sw = new StringWriter();
        new JsonWriter(sw, RuntimeObjectDescriptor.For).WriteElement(Describe(Sample), Sample);

        Assert.That(sw.ToString(), Is.EqualTo("""{"Shown":"shown","Internal":"internal"}"""));
    }

    [Test]
    public async Task HiddenFieldIsAbsentFromTableAndWide()
    {
        foreach (var wide in new[] { false, true })
        {
            var sw = new StringWriter();
            var provider = new TableObjectFormatterProvider(Options.Create(new TableOutputOptions { Wide = wide }));
            var formatter = await provider.CreateFormatterAsync<Secretive>(Describe(Sample), sw, collection: true);
            await formatter.OutputElementAsync(Sample);
            await ((IAsyncDisposable)formatter).DisposeAsync();

            Assert.That(sw.ToString(), Does.Not.Contain("s3cr3t"), $"wide: {wide}");
            Assert.That(sw.ToString(), Does.Not.Contain("SECRET"), $"wide: {wide}");
        }
    }

    [Test]
    public void HiddenFieldIsAbsentAtDepth()
    {
        var value = new Wrapper("w", Sample);
        var sw = new StringWriter();
        new YamlWriter(sw, RuntimeObjectDescriptor.For).WriteElement(Describe(value), value, sequenceItem: false);

        Assert.That(sw.ToString(), Does.Not.Contain("s3cr3t"), "a nested descriptor must drop it too");
        Assert.That(sw.ToString(), Does.Contain("Internal: internal"), "Internal stays visible in the data formats");
    }

    /// <summary>
    /// <c>Filter</c> falls back to the full set when a level would hide everything, so that
    /// a table is never empty. That fallback must not resurrect a Hidden field.
    /// </summary>
    [Test]
    public async Task HiddenFieldDoesNotComeBackWhenEverythingElseIsFiltered()
    {
        var value = new AllHidden("s3cr3t");
        var descriptor = Describe(value);

        Assert.That(descriptor.Fields, Is.Empty);

        var sw = new StringWriter();
        var provider = new TableObjectFormatterProvider(Options.Create(new TableOutputOptions()));
        var formatter = await provider.CreateFormatterAsync<AllHidden>(descriptor, sw, collection: true);
        await formatter.OutputElementAsync(value);
        await ((IAsyncDisposable)formatter).DisposeAsync();

        Assert.That(sw.ToString(), Does.Not.Contain("s3cr3t"));
    }
}
