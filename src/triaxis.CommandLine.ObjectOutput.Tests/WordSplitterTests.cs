namespace triaxis.CommandLine.ObjectOutput.Tests;

using triaxis.CommandLine.ObjectOutput.Helpers;

[TestFixture]
public class WordSplitterTests
{
    private static string[] Split(string input)
    {
        var ws = new WordSplitter(input.AsSpan());
        var list = new List<string>();
        while (ws.NextWord() is { IsEmpty: false } word)
        {
            list.Add(word.ToString());
        }
        return list.ToArray();
    }

    [Test]
    public void Split_SnakeCase()
        => Assert.That(Split("bla_bla"), Is.EqualTo(new[] { "bla", "bla" }));

    [Test]
    public void Split_PascalCase()
        => Assert.That(Split("BlaBla"), Is.EqualTo(new[] { "Bla", "Bla" }));

    [Test]
    public void Split_AcronymSuffix()
        => Assert.That(Split("TestID"), Is.EqualTo(new[] { "Test", "ID" }));

    [Test]
    public void Split_MixedLettersDigitsAcronyms()
        => Assert.That(Split("Test1THING"), Is.EqualTo(new[] { "Test", "1", "THING" }));

    [Test]
    public void Split_StripsNonLetterDigitCharacters()
        => Assert.That(Split("  hello, world!"), Is.EqualTo(new[] { "hello", "world" }));

    [Test]
    public void Split_EmptyString()
        => Assert.That(Split(""), Is.Empty);

    [Test]
    public void Split_SingleLetter()
        => Assert.That(Split("A"), Is.EqualTo(new[] { "A" }));

    [Test]
    public void Split_BracketedAcronyms()
        => Assert.That(Split("what[Is]THIsX"), Is.EqualTo(new[] { "what", "Is", "TH", "Is", "X" }));
}
