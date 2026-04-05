namespace triaxis.CommandLine.Serilog.Tests;

using System.CommandLine;
using Microsoft.Extensions.Logging;

[TestFixture]
public class VerbosityOptionsTests
{
    private static RootCommand CreateRoot()
    {
        var root = new RootCommand();
        root.Options.Add(VerbosityOptions.Verbosity);
        root.Options.Add(VerbosityOptions.Verbose);
        root.Options.Add(VerbosityOptions.Quiet);
        return root;
    }

    [Test]
    public void DefaultLevel_IsInformation()
    {
        var root = CreateRoot();
        var result = root.Parse([]);
        Assert.That(VerbosityOptions.GetEffectiveLevel(result), Is.EqualTo(LogLevel.Information));
    }

    [Test]
    public void ExplicitVerbosity_IsHonored()
    {
        var root = CreateRoot();
        var result = root.Parse(["--verbosity", "Warning"]);
        Assert.That(VerbosityOptions.GetEffectiveLevel(result), Is.EqualTo(LogLevel.Warning));
    }

    [Test]
    public void SingleVerbose_DecreasesLevelFromInformation_ToDebug()
    {
        var root = CreateRoot();
        var result = root.Parse(["-v"]);
        Assert.That(VerbosityOptions.GetEffectiveLevel(result), Is.EqualTo(LogLevel.Debug));
    }

    [Test]
    public void DoubleVerbose_DecreasesLevelFromInformation_ToTrace()
    {
        var root = CreateRoot();
        var result = root.Parse(["-v", "-v"]);
        Assert.That(VerbosityOptions.GetEffectiveLevel(result), Is.EqualTo(LogLevel.Trace));
    }

    [Test]
    public void SingleQuiet_IncreasesLevelFromInformation_ToWarning()
    {
        var root = CreateRoot();
        var result = root.Parse(["-q"]);
        Assert.That(VerbosityOptions.GetEffectiveLevel(result), Is.EqualTo(LogLevel.Warning));
    }

    [Test]
    public void DoubleQuiet_IncreasesLevelFromInformation_ToError()
    {
        var root = CreateRoot();
        var result = root.Parse(["-q", "-q"]);
        Assert.That(VerbosityOptions.GetEffectiveLevel(result), Is.EqualTo(LogLevel.Error));
    }

    [Test]
    public void ExplicitVerbosityAndVerbose_Combine()
    {
        var root = CreateRoot();
        var result = root.Parse(["--verbosity", "Warning", "-v"]);
        // Warning - 1 = Information
        Assert.That(VerbosityOptions.GetEffectiveLevel(result), Is.EqualTo(LogLevel.Information));
    }

    [Test]
    public void VerbosityOptionIsRecursive()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VerbosityOptions.Verbosity.Recursive, Is.True);
            Assert.That(VerbosityOptions.Verbose.Recursive, Is.True);
            Assert.That(VerbosityOptions.Quiet.Recursive, Is.True);
        });
    }
}
