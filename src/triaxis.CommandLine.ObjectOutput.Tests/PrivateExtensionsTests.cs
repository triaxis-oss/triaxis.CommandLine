namespace triaxis.CommandLine.ObjectOutput.Tests;

[TestFixture]
public class PrivateExtensionsTests
{
    [TestCase("bla_bla", "BLA BLA")]
    [TestCase("BlaBla", "BLA BLA")]
    [TestCase("TestID", "TEST ID")]
    [TestCase("Test1THING", "TEST 1 THING")]
    [TestCase("what[Is]THIsX", "WHAT IS TH IS X")]
    [TestCase("  WS around 4 Words...", "WS AROUND 4 WORDS")]
    [TestCase("", "")]
    [TestCase("A", "A")]
    [TestCase("single", "SINGLE")]
    public void ToTableTitle_TransformsToUpperCaseWithSpaceSeparators(string input, string expected)
    {
        Assert.That(input.ToTableTitle(), Is.EqualTo(expected));
    }
}
