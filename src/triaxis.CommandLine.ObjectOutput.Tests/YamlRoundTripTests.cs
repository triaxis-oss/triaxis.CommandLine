namespace triaxis.CommandLine.ObjectOutput.Tests;

using System.Text;
using triaxis.CommandLine.ObjectOutput.Formatters;
using YamlDotNet.Serialization;

/// <summary>
/// Emits every corpus string and reads it back with an independent parser. A failure
/// here means the emitter produced YAML that a consumer would read as a different
/// value — the one class of bug quoting exists to prevent.
/// </summary>
[TestFixture]
public class YamlRoundTripTests
{
    private record Holder(string V);

    // Without this YamlDotNet hands back every plain scalar as a string when the target
    // is object, which would reduce this to a test of structural validity only.
    private static readonly IDeserializer s_parser = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    private static string Emit(string value)
    {
        var descriptor = new DefaultObjectDescriptorProvider<Holder>().GetDescriptor(new Holder(""));
        var sw = new StringWriter();
        new YamlWriter(sw, RuntimeObjectDescriptor.For).WriteElement(descriptor, new Holder(value), sequenceItem: false);
        return sw.ToString();
    }

    private static bool RoundTrips(string value, out string yaml, out object? parsed)
    {
        yaml = Emit(value);

        try
        {
            var map = s_parser.Deserialize<Dictionary<string, object>>(yaml);
            parsed = map is not null && map.TryGetValue("V", out var v) ? v : null;
        }
        catch (Exception ex)
        {
            parsed = ex.GetType().Name;
            return false;
        }

        return parsed is string s && s == value;
    }

    public static IEnumerable<TestCaseData> CuratedCases()
        => YamlCorpus.Curated()
            .Distinct()
            .Select((s, i) => new TestCaseData(s).SetName($"Curated_{i:D3}_{Escape(s)}"));

    [TestCaseSource(nameof(CuratedCases))]
    public void CuratedValue_RoundTrips(string value)
    {
        Assert.That(RoundTrips(value, out var yaml, out var parsed), Is.True,
            $"emitted {Escape(yaml)} which parsed back as {parsed?.GetType().Name ?? "null"} {Escape(parsed as string ?? parsed?.ToString() ?? "")}");
    }

    [Test]
    public void FuzzedValues_RoundTrip()
    {
        var failures = new List<string>();

        foreach (var value in YamlCorpus.Fuzz(20000))
        {
            if (!RoundTrips(value, out var yaml, out var parsed))
            {
                failures.Add($"{Escape(value)} -> {Escape(yaml)} -> {Escape(parsed as string ?? parsed?.ToString() ?? "null")}");
            }
        }

        Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
    }

    /// Renders control characters visibly so a failure message is readable and the
    /// NUnit test name stays a single line.
    private static string Escape(string s)
    {
        var sb = new StringBuilder("'");

        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c < 0x20 || c == 0x7F ? $"\\x{(int)c:x2}" : c.ToString(),
            });
        }

        return sb.Append('\'').ToString();
    }
}
