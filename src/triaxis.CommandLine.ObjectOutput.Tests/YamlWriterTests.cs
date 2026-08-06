namespace triaxis.CommandLine.ObjectOutput.Tests;

using triaxis.CommandLine.ObjectOutput.Formatters;

[TestFixture]
public class YamlWriterTests
{
    private record Endpoint(string Host, int Port);
    private record Service(string Name, Endpoint Listen);
    private record Cluster(string Name, List<Endpoint> Members);
    private record Bag(string Name, List<string> Tags);
    private record Doc(string Text);
    private record Numbers(int Count, double Ratio, double Whole, double Missing);
    private record Money(decimal Exact, decimal Round);
    private record Maybe(string? Value);

    private static string Emit<T>(T value, bool sequenceItem = false)
    {
        var descriptor = new DefaultObjectDescriptorProvider<T>().GetDescriptor(value);
        var sw = new StringWriter();
        new YamlWriter(sw, RuntimeObjectDescriptor.For).WriteElement(descriptor!, value!, sequenceItem);
        return sw.ToString();
    }

    [Test]
    public void NestedObject_BecomesIndentedMapping()
    {
        Assert.That(Emit(new Service("api", new Endpoint("localhost", 8080))), Is.EqualTo(
            "Name: api\n" +
            "Listen:\n" +
            "  Host: localhost\n" +
            "  Port: 8080\n"));
    }

    [Test]
    public void NestedObjectSequence_InlinesFirstKeyAfterDash()
    {
        Assert.That(Emit(new Cluster("c1", [new Endpoint("a", 1), new Endpoint("b", 2)])), Is.EqualTo(
            "Name: c1\n" +
            "Members:\n" +
            "  - Host: a\n" +
            "    Port: 1\n" +
            "  - Host: b\n" +
            "    Port: 2\n"));
    }

    [Test]
    public void ScalarSequence_BecomesBlockSequence()
    {
        Assert.That(Emit(new Bag("b", ["one", "two"])), Is.EqualTo(
            "Name: b\n" +
            "Tags:\n" +
            "  - one\n" +
            "  - two\n"));
    }

    [Test]
    public void EmptySequence_BecomesFlowEmpty()
    {
        Assert.That(Emit(new Bag("b", [])), Is.EqualTo("Name: b\nTags: []\n"));
    }

    [Test]
    public void ElementAsSequenceItem_IndentsContinuationKeys()
    {
        Assert.That(Emit(new Endpoint("a", 1), sequenceItem: true), Is.EqualTo("- Host: a\n  Port: 1\n"));
    }

    [Test]
    public void MultiLineString_BecomesLiteralBlock()
    {
        Assert.That(Emit(new Doc("line one\nline two\n")), Is.EqualTo(
            "Text: |\n" +
            "  line one\n" +
            "  line two\n"));
    }

    [Test]
    public void MultiLineStringWithoutTrailingNewline_UsesStripChomping()
    {
        Assert.That(Emit(new Doc("line one\nline two")), Is.EqualTo(
            "Text: |-\n" +
            "  line one\n" +
            "  line two\n"));
    }

    [Test]
    public void CarriageReturn_FallsBackToQuoted()
    {
        // block scalars normalise line breaks, so \r\n would not survive
        Assert.That(Emit(new Doc("a\r\nb")), Is.EqualTo("Text: \"a\\r\\nb\"\n"));
    }

    [Test]
    public void Numbers_AreNeverQuotedAndKeepTheirType()
    {
        Assert.That(Emit(new Numbers(3, 1.5, 2.0, double.NaN)), Is.EqualTo(
            "Count: 3\n" +
            "Ratio: 1.5\n" +
            "Whole: 2.0\n" +
            "Missing: .nan\n"));
    }

    [Test]
    public void WholeDecimal_KeepsAFractionSoItStaysAFloat()
    {
        Assert.That(Emit(new Money(19.8m, 17m)), Is.EqualTo("Exact: 19.8\nRound: 17.0\n"));
    }

    [Test]
    public void NullValue_BecomesNullLiteral()
    {
        Assert.That(Emit(new Maybe(null)), Is.EqualTo("Value: null\n"));
    }

    [TestCase("hello world")]
    [TestCase("v1.2.3-beta")]
    [TestCase("path/to/file")]
    [TestCase("user@example.com")]
    [TestCase("N/A")]
    [TestCase("<none>")]
    [TestCase("80/TCP")]
    [TestCase("--flag")]
    [TestCase("::1")]
    [TestCase("--")]
    [TestCase("100%")]
    [TestCase("café")]
    public void OrdinaryValue_StaysUnquoted(string value)
    {
        Assert.That(PlainScalar.IsSafe(value), Is.True);
        Assert.That(Emit(new Doc(value)), Is.EqualTo($"Text: {value}\n"));
    }

    [TestCase("yes", TestName = "Quoted_yes_is_bool_in_YAML_1_1")]
    [TestCase("no", TestName = "Quoted_no_is_bool_in_YAML_1_1")]
    [TestCase("null", TestName = "Quoted_null")]
    [TestCase("true", TestName = "Quoted_true")]
    [TestCase("y", TestName = "Quoted_y_is_bool_in_Psych")]
    [TestCase("1:30:00", TestName = "Quoted_sexagesimal_TimeSpan_shape")]
    [TestCase("2026-08-06", TestName = "Quoted_timestamp")]
    [TestCase("1e10", TestName = "Quoted_float_in_1_2_but_string_in_1_1")]
    [TestCase("007", TestName = "Quoted_octal_with_leading_zero")]
    [TestCase("a: b", TestName = "Quoted_colon_space_ends_the_key")]
    [TestCase("a #c", TestName = "Quoted_space_hash_starts_a_comment")]
    [TestCase("- dash", TestName = "Quoted_dash_space_starts_a_sequence")]
    [TestCase("-", TestName = "Quoted_bare_dash_is_a_sequence_indicator")]
    [TestCase("trailing ", TestName = "Quoted_trailing_space_is_stripped")]
    [TestCase("", TestName = "Quoted_empty_string")]
    public void AmbiguousValue_IsQuoted(string value)
    {
        Assert.That(PlainScalar.IsSafe(value), Is.False);
        Assert.That(Emit(new Doc(value)), Does.StartWith("Text: \""));
    }
}
