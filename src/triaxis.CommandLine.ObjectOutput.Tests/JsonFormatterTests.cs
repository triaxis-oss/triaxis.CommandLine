namespace triaxis.CommandLine.ObjectOutput.Tests;

using triaxis.CommandLine.ObjectOutput.Formatters;

[TestFixture]
public class JsonFormatterTests
{
    public record Item(string Name, int Count);

    [Test]
    public async Task OutputsSingleObject_AsJsonObject()
    {
        var provider = new DefaultObjectDescriptorProvider<Item>();
        var descriptor = provider.GetDescriptor(new Item("apple", 3));
        var sw = new StringWriter();
        var fmt = new JsonObjectFormatterProvider();

        await using var scope = new AsyncScope(sw);
        var formatter = await fmt.CreateFormatterAsync<Item>(descriptor, sw, collection: false);
        await formatter.OutputElementAsync(new Item("apple", 3));
        await ((IAsyncDisposable)formatter).DisposeAsync();

        var json = sw.ToString().Trim();
        Assert.That(json, Does.Contain("\"Name\""));
        Assert.That(json, Does.Contain("\"apple\""));
        Assert.That(json, Does.Contain("\"Count\""));
        Assert.That(json, Does.Contain("3"));
        Assert.That(json.StartsWith("{"), Is.True);
        Assert.That(json.EndsWith("}"), Is.True);
    }

    [Test]
    public async Task OutputsCollection_AsJsonArray()
    {
        var provider = new DefaultObjectDescriptorProvider<Item>();
        var descriptor = provider.GetDescriptor(new Item("x", 0));
        var sw = new StringWriter();
        var fmt = new JsonObjectFormatterProvider();

        var formatter = await fmt.CreateFormatterAsync<Item>(descriptor, sw, collection: true);
        await formatter.OutputElementAsync(new Item("one", 1));
        await formatter.OutputElementAsync(new Item("two", 2));
        await ((IAsyncDisposable)formatter).DisposeAsync();

        var json = sw.ToString().Trim();
        Assert.That(json.StartsWith("["), Is.True);
        Assert.That(json.EndsWith("]"), Is.True);
        Assert.That(json, Does.Contain("one"));
        Assert.That(json, Does.Contain("two"));
    }

    // helper to satisfy `using` scope in test
    private sealed class AsyncScope : IAsyncDisposable
    {
        private readonly StringWriter _sw;
        public AsyncScope(StringWriter sw) { _sw = sw; }
        public ValueTask DisposeAsync()
        {
            _sw.Dispose();
            return default;
        }
    }
}
