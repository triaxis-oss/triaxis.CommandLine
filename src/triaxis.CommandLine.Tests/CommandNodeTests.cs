namespace triaxis.CommandLine.Tests;

using System.CommandLine;

[TestFixture]
public class CommandNodeTests
{
    [Test]
    public void GetCommand_CreatesIntermediateNodes()
    {
        var root = new RootCommand();
        var node = new CommandNode(root);

        var leaf = node.GetCommand(["a", "b", "c"]);
        Assert.That(leaf.Name, Is.EqualTo("c"));
    }

    [Test]
    public void GetCommand_ReturnsCachedNodesForSamePath()
    {
        var root = new RootCommand();
        var node = new CommandNode(root);

        var first = node.GetCommand(["x", "y"]);
        var second = node.GetCommand(["x", "y"]);
        Assert.That(first, Is.SameAs(second));
    }

    [Test]
    public void Realize_AttachesAllChildCommandsToTheTree()
    {
        var root = new RootCommand();
        var node = new CommandNode(root);

        node.GetCommand(["alpha"]);
        node.GetCommand(["beta", "gamma"]);

        node.Realize();

        Assert.That(root.Subcommands.Select(c => c.Name), Does.Contain("alpha"));
        Assert.That(root.Subcommands.Select(c => c.Name), Does.Contain("beta"));
        var beta = root.Subcommands.First(c => c.Name == "beta");
        Assert.That(beta.Subcommands.Select(c => c.Name), Does.Contain("gamma"));
    }

    [Test]
    public void Realize_IsIdempotent()
    {
        var root = new RootCommand();
        var node = new CommandNode(root);
        node.GetCommand(["alpha"]);
        node.Realize();
        var countAfterFirst = root.Subcommands.Count;
        node.Realize();
        Assert.That(root.Subcommands.Count, Is.EqualTo(countAfterFirst));
    }

    [Test]
    public void GetCommand_IsCaseInsensitiveForPathSegments()
    {
        var root = new RootCommand();
        var node = new CommandNode(root);

        var lower = node.GetCommand(["foo"]);
        var upper = node.GetCommand(["FOO"]);
        Assert.That(upper, Is.SameAs(lower));
    }
}
