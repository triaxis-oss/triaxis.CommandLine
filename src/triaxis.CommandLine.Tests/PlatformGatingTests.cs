namespace triaxis.CommandLine.Tests;

using System.CommandLine;
using System.Runtime.InteropServices;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

// Platform-gated commands used by PlatformGatingTests. The source generator emits
// an OperatingSystem.IsXxx() OR chain as IsSupported on the corresponding
// CommandTreeNode, and ApplyTo skips children whose IsSupported is false.
//
// SupportedOSPlatform is a .NET 5+ attribute and the PolySharp polyfill is emitted
// as an internal type that can't be used here on .NET Framework, so the gated
// commands and their tests only compile for net5.0+ target frameworks.

#if NET5_0_OR_GREATER

[Command("platform-any")]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class PlatformAnyCommand
{
    public Task ExecuteAsync() => Task.CompletedTask;
}

#pragma warning disable CA1416 // Platform compatibility (generated code handles gating via IsSupported)
#pragma warning disable CA1418 // 'never-a-real-os' is not a known platform name — intentional for the "filtered out" test
[Command("platform-fake")]
[SupportedOSPlatform("never-a-real-os")]
public class PlatformFakeCommand
{
    public Task ExecuteAsync() => Task.CompletedTask;
}
#pragma warning restore CA1418
#pragma warning restore CA1416

[Command("platform-windows")]
[SupportedOSPlatform("windows")]
public class PlatformWindowsCommand
{
    public Task ExecuteAsync() => Task.CompletedTask;
}

[Command("platform-linux")]
[SupportedOSPlatform("linux")]
public class PlatformLinuxCommand
{
    public Task ExecuteAsync() => Task.CompletedTask;
}

[Command("platform-macos")]
[SupportedOSPlatform("macos")]
public class PlatformMacOsCommand
{
    public Task ExecuteAsync() => Task.CompletedTask;
}

// Base class carries [SupportedOSPlatform]; the derived command should inherit the
// gating even though SupportedOSPlatformAttribute has Inherited=false — the generator
// walks the base-type chain explicitly for this case.
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public abstract class AllPlatformsCommandBase
{
    public Task ExecuteAsync() => Task.CompletedTask;
}

[Command("platform-inherited")]
public class PlatformInheritedCommand : AllPlatformsCommandBase
{
}

#pragma warning disable CA1416
#pragma warning disable CA1418
[SupportedOSPlatform("never-a-real-os")]
public abstract class NeverCommandBase
{
    public Task ExecuteAsync() => Task.CompletedTask;
}
#pragma warning restore CA1418
#pragma warning restore CA1416

[Command("platform-inherited-fake")]
public class PlatformInheritedFakeCommand : NeverCommandBase
{
}

// Derived-most-wins: the derived class supplies its own platform set, hiding the
// base's constraint. The command must register on any desktop host.
[Command("platform-derived-wins")]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class PlatformDerivedWinsCommand : NeverCommandBase
{
}

[TestFixture]
public class PlatformGatingTests
{
    private static string[] SubcommandNames()
    {
        var builder = Tool.CreateBuilder([]);
        builder.AddCommandsFromAssembly(typeof(PlatformGatingTests).Assembly);
        return builder.RootCommand.Subcommands.Select(c => c.Name).ToArray();
    }

    [Test]
    public void CommandWithMultipleSupportedPlatforms_IsRegistered_WhenAnyMatches()
    {
        Assert.That(SubcommandNames(), Does.Contain("platform-any"));
    }

    [Test]
    public void CommandWithUnknownPlatform_IsFilteredOut()
    {
        Assert.That(SubcommandNames(), Does.Not.Contain("platform-fake"));
    }

    [Test]
    public void WindowsOnlyCommand_RegisteredOnlyOnWindows()
    {
        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        Assert.That(SubcommandNames().Contains("platform-windows"), Is.EqualTo(expected));
    }

    [Test]
    public void LinuxOnlyCommand_RegisteredOnlyOnLinux()
    {
        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        Assert.That(SubcommandNames().Contains("platform-linux"), Is.EqualTo(expected));
    }

    [Test]
    public void MacOsOnlyCommand_RegisteredOnlyOnMacOs()
    {
        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        Assert.That(SubcommandNames().Contains("platform-macos"), Is.EqualTo(expected));
    }

    [Test]
    public void SupportedOSPlatform_IsDetectedOnBaseClass()
    {
        Assert.That(SubcommandNames(), Does.Contain("platform-inherited"));
    }

    [Test]
    public void SupportedOSPlatform_BaseClassGatingFiltersOut()
    {
        Assert.That(SubcommandNames(), Does.Not.Contain("platform-inherited-fake"));
    }

    [Test]
    public void SupportedOSPlatform_DerivedMostTypeWinsOverBase()
    {
        // Base has an unreachable-platform constraint; the derived class supplies its
        // own windows|linux|macos set. The derived set must win, so the command
        // registers on any desktop host.
        Assert.That(SubcommandNames(), Does.Contain("platform-derived-wins"));
    }
}

#endif // NET5_0_OR_GREATER

[TestFixture]
public class CommandTreeNodeIsSupportedTests
{
    private sealed class StubAction : System.CommandLine.Invocation.AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
            => Task.FromResult(0);
    }

    private static CommandTreeNode Leaf(string name, bool supported = true) =>
        new(name) { Action = new StubAction(), IsSupported = supported };

    [Test]
    public void IsSupported_DefaultsToTrue()
    {
        var node = new CommandTreeNode("x");
        Assert.That(node.IsSupported, Is.True);
    }

    [Test]
    public void ApplyTo_SkipsChildrenMarkedUnsupported()
    {
        var parent = new Command("root");
        var tree = new CommandTreeNode("")
        {
            Subcommands =
            {
                Leaf("kept"),
                Leaf("skipped", supported: false),
            },
        };

        tree.ApplyTo(parent);

        Assert.That(parent.Subcommands.Select(c => c.Name), Is.EquivalentTo(new[] { "kept" }));
    }

    [Test]
    public void ApplyTo_KeepsChildrenMarkedSupported()
    {
        var parent = new Command("root");
        var tree = new CommandTreeNode("")
        {
            Subcommands = { Leaf("a"), Leaf("b") },
        };

        tree.ApplyTo(parent);

        Assert.That(parent.Subcommands.Select(c => c.Name), Is.EquivalentTo(new[] { "a", "b" }));
    }

    [Test]
    public void ApplyTo_TrimsIntermediateNodeWhenAllChildrenGatedOut()
    {
        var parent = new Command("root");
        var tree = new CommandTreeNode("")
        {
            Subcommands =
            {
                new CommandTreeNode("group")
                {
                    Subcommands =
                    {
                        Leaf("win", supported: false),
                        Leaf("nix", supported: false),
                    },
                },
            },
        };

        tree.ApplyTo(parent);

        Assert.That(parent.Subcommands, Is.Empty);
    }

    [Test]
    public void ApplyTo_KeepsIntermediateNodeWhenAnyChildSupported()
    {
        var parent = new Command("root");
        var tree = new CommandTreeNode("")
        {
            Subcommands =
            {
                new CommandTreeNode("group")
                {
                    Subcommands =
                    {
                        Leaf("win", supported: false),
                        Leaf("nix"),
                    },
                },
            },
        };

        tree.ApplyTo(parent);

        var group = parent.Subcommands.Single();
        Assert.That(group.Name, Is.EqualTo("group"));
        Assert.That(group.Subcommands.Select(c => c.Name), Is.EquivalentTo(new[] { "nix" }));
    }

    [Test]
    public void ApplyTo_TrimsNestedIntermediateNodesRecursively()
    {
        var parent = new Command("root");
        var tree = new CommandTreeNode("")
        {
            Subcommands =
            {
                new CommandTreeNode("outer")
                {
                    Subcommands =
                    {
                        new CommandTreeNode("inner")
                        {
                            Subcommands = { Leaf("leaf", supported: false) },
                        },
                    },
                },
            },
        };

        tree.ApplyTo(parent);

        Assert.That(parent.Subcommands, Is.Empty);
    }

    [Test]
    public void ApplyTo_KeepsActionNodeEvenWhenAllChildrenGatedOut()
    {
        var parent = new Command("root");
        var withAction = Leaf("cmd");
        withAction.Subcommands.Add(Leaf("sub", supported: false));
        var tree = new CommandTreeNode("") { Subcommands = { withAction } };

        tree.ApplyTo(parent);

        var cmd = parent.Subcommands.Single();
        Assert.That(cmd.Name, Is.EqualTo("cmd"));
        Assert.That(cmd.Subcommands, Is.Empty);
    }
}
