namespace triaxis.CommandLine.ObjectOutput.Tests;

[TestFixture]
public class PrivateExtensionsTests
{
    [Test]
    public void Test()
    {
        Assert.That("bla_bla".ToTableTitle(), Is.EqualTo("BLA BLA"));
        Assert.That("BlaBla".ToTableTitle(), Is.EqualTo("BLA BLA"));
        Assert.That("TestID".ToTableTitle(), Is.EqualTo("TEST ID"));
        Assert.That("Test1THING".ToTableTitle(), Is.EqualTo("TEST 1 THING"));
        Assert.That("what[Is]THIsX".ToTableTitle(), Is.EqualTo("WHAT IS TH IS X"));
        Assert.That("  WS around 4 Words...".ToTableTitle(), Is.EqualTo("WS AROUND 4 WORDS"));
    }
}
