namespace triaxis.CommandLine.ObjectOutput.Tests;

using System.Text.Json;
using triaxis.CommandLine.ObjectOutput.Formatters;

[TestFixture]
public class JsonWriterTests
{
    private record Endpoint(string Host, int Port);
    private record Service(string Name, Endpoint Listen);
    private record Cluster(string Name, List<Endpoint> Members);
    private record Bag(string Name, List<string> Tags);
    private record Numbers(int Count, double Ratio, double Missing, decimal Money);
    private record Maybe(string? Value);
    private record Text(string Value);

    private static string Emit<T>(T value)
    {
        var descriptor = new DefaultObjectDescriptorProvider<T>().GetDescriptor(value);
        var sw = new StringWriter();
        new JsonWriter(sw, RuntimeObjectDescriptor.For).WriteElement(descriptor!, value!);
        return sw.ToString();
    }

    /// Every emitted document must be readable by an independent parser — the emitter is
    /// hand-written, so "it looks like JSON" is not evidence.
    private static void AssertParses(string json)
    {
        Assert.DoesNotThrow(() => JsonDocument.Parse(json), $"not valid JSON: {json}");
    }

    [Test]
    public void NestedObject_UsesItsOwnDescriptor()
    {
        var json = Emit(new Service("api", new Endpoint("localhost", 8080)));

        AssertParses(json);
        Assert.That(json, Is.EqualTo("""{"Name":"api","Listen":{"Host":"localhost","Port":8080}}"""));
    }

    [Test]
    public void NestedCollection_BecomesArrayOfObjects()
    {
        var json = Emit(new Cluster("c1", [new Endpoint("a", 1), new Endpoint("b", 2)]));

        AssertParses(json);
        Assert.That(json, Is.EqualTo("""{"Name":"c1","Members":[{"Host":"a","Port":1},{"Host":"b","Port":2}]}"""));
    }

    [Test]
    public void ScalarCollectionAndEmptyCollection()
    {
        AssertParses(Emit(new Bag("b", ["one", "two"])));
        Assert.That(Emit(new Bag("b", ["one", "two"])), Is.EqualTo("""{"Name":"b","Tags":["one","two"]}"""));
        Assert.That(Emit(new Bag("b", [])), Is.EqualTo("""{"Name":"b","Tags":[]}"""));
    }

    [Test]
    public void NumbersStayUnquoted_AndKeepInvariantForm()
    {
        var json = Emit(new Numbers(3, 1.5, 0, 19.80m));

        AssertParses(json);
        Assert.That(json, Is.EqualTo("""{"Count":3,"Ratio":1.5,"Missing":0,"Money":19.80}"""));
    }

    [Test]
    public void NonFiniteDouble_BecomesAStringRatherThanInvalidJson()
    {
        var json = Emit(new Numbers(0, double.NaN, double.PositiveInfinity, 0m));

        AssertParses(json);
        Assert.That(json, Does.Contain("\"NaN\"").And.Contain("\"Infinity\""));
    }

    [Test]
    public void NullValue_BecomesNullLiteral()
    {
        Assert.That(Emit(new Maybe(null)), Is.EqualTo("""{"Value":null}"""));
    }

    [TestCase("plain", "\"plain\"")]
    [TestCase("with \"quotes\"", "\"with \\\"quotes\\\"\"")]
    [TestCase("back\\slash", "\"back\\\\slash\"")]
    [TestCase("line\nbreak", "\"line\\nbreak\"")]
    [TestCase("tab\there", "\"tab\\there\"")]
    [TestCase("bell", "\"bell\\u0007\"")]
    public void StringsAreEscaped(string value, string expected)
    {
        var json = Emit(new Text(value));

        AssertParses(json);
        Assert.That(json, Is.EqualTo($$"""{"Value":{{expected}}}"""));
    }

    [Test]
    public void NonAsciiIsWrittenLiterally()
    {
        var json = Emit(new Text("café 日本 🚀"));

        AssertParses(json);
        Assert.That(json, Does.Contain("café 日本 🚀"), "escaping non-ASCII would only hurt readability");
        Assert.That(JsonDocument.Parse(json).RootElement.GetProperty("Value").GetString(), Is.EqualTo("café 日本 🚀"));
    }

    [Test]
    public void UnpairedSurrogateIsEscaped()
    {
        // it cannot be encoded as UTF-8, so writing it literally would lose it to U+FFFD
        var json = Emit(new Text("lone \ud800 half"));

        AssertParses(json);
        Assert.That(json, Does.Contain("\\ud800"));
    }

    [Test]
    public void EveryCorpusStringRoundTrips()
    {
        var failures = new List<string>();

        foreach (var value in YamlCorpus.Curated().Concat(YamlCorpus.Fuzz(5000)).Distinct())
        {
            var json = Emit(new Text(value));

            try
            {
                var read = JsonDocument.Parse(json).RootElement.GetProperty("Value").GetString();
                if (read != value)
                {
                    failures.Add($"{json} -> {read}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{json} -> {ex.GetType().Name}");
            }
        }

        Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
    }
}
