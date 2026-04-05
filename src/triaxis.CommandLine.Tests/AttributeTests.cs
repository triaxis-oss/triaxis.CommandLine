namespace triaxis.CommandLine.Tests;

[TestFixture]
public class AttributeTests
{
    [Test]
    public void CommandAttribute_StoresPathAndMetadata()
    {
        var attr = new CommandAttribute("a", "b") { Aliases = ["alias1"], Description = "desc" };
        Assert.That(attr.Path, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(attr.Aliases, Is.EqualTo(new[] { "alias1" }));
        Assert.That(attr.Description, Is.EqualTo("desc"));
    }

    [Test]
    public void OptionAttribute_AliasesConstructor()
    {
        var attr = new OptionAttribute("--long", "-l", "-ll");
        Assert.That(attr.Name, Is.EqualTo("--long"));
        Assert.That(attr.Aliases, Is.EqualTo(new[] { "-l", "-ll" }));
    }

    [Test]
    public void OptionAttribute_RequiredDefaultsToFalseButTracksSet()
    {
        var attr = new OptionAttribute();
        Assert.That(attr.Required, Is.False);
        Assert.That(attr.RequiredIsSet, Is.False);

        attr.Required = true;
        Assert.That(attr.Required, Is.True);
        Assert.That(attr.RequiredIsSet, Is.True);

        attr.Required = false;
        Assert.That(attr.Required, Is.False);
        Assert.That(attr.RequiredIsSet, Is.True);
    }

    [Test]
    public void ArgumentAttribute_RequiredTracksSet()
    {
        var attr = new ArgumentAttribute();
        Assert.That(attr.RequiredIsSet, Is.False);
        attr.Required = true;
        Assert.That(attr.Required, Is.True);
        Assert.That(attr.RequiredIsSet, Is.True);
    }

    [Test]
    public void InjectAttribute_CanSpecifyExplicitType()
    {
        var attr = new InjectAttribute(typeof(string));
        Assert.That(attr.Type, Is.EqualTo(typeof(string)));
    }

    [Test]
    public void CommandErrorException_RetainsMessageArguments()
    {
        var ex = new CommandErrorException("error {Code} at {Where}", 42, "here");
        Assert.That(ex.Message, Is.EqualTo("error {Code} at {Where}"));
        Assert.That(ex.MessageArguments, Is.EqualTo(new object?[] { 42, "here" }));
    }
}
