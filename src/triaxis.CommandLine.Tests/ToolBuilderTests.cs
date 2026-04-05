namespace triaxis.CommandLine.Tests;

using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[TestFixture]
public class ToolBuilderTests
{
    [Test]
    public void CreateBuilder_ReturnsNonNullBuilder()
    {
        var builder = Tool.CreateBuilder(["a", "b"]);
        Assert.That(builder, Is.Not.Null);
        Assert.That(builder.Arguments, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void Builder_RootCommandIsAccessible()
    {
        var builder = Tool.CreateBuilder([]);
        Assert.That(builder.RootCommand, Is.Not.Null.And.InstanceOf<RootCommand>());
    }

    [Test]
    public void Builder_ConfigurationIsAccessible()
    {
        var builder = Tool.CreateBuilder([]);
        Assert.That(builder.Configuration, Is.Not.Null.And.InstanceOf<IConfigurationManager>());
    }

    [Test]
    public void GetCommand_CreatesNestedCommandsByPath()
    {
        var builder = Tool.CreateBuilder([]);
        var leaf = builder.GetCommand("foo", "bar");
        Assert.That(leaf, Is.Not.Null);
        Assert.That(leaf.Name, Is.EqualTo("bar"));

        // Same path returns same command instance
        var leaf2 = builder.GetCommand("foo", "bar");
        Assert.That(leaf2, Is.SameAs(leaf));

        // Parent command also reachable via separate GetCommand call
        var parent = builder.GetCommand("foo");
        Assert.That(parent.Name, Is.EqualTo("foo"));
    }

    [Test]
    public void ConfigureServices_AllowsServiceRegistration()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.ConfigureServices(services => services.AddSingleton("hello"));
        Assert.That(result, Is.SameAs(builder), "ConfigureServices should return the same builder for chaining");
    }

    [Test]
    public void AddMiddleware_ReturnsSameBuilderForChaining()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.AddMiddleware(async (ctx, next) => await next(ctx));
        Assert.That(result, Is.SameAs(builder));
    }

    [Test]
    public void GetServiceProviderAccessor_BeforeRun_ThrowsOrReturnsNull()
    {
        var builder = Tool.CreateBuilder([]);
        var accessor = builder.GetServiceProviderAccessor();
        Assert.That(accessor, Is.Not.Null);
        // Before Run, accessor returns null (the backing field is not yet populated).
        // Calling it should not throw — the delegate uses the null-forgiving operator.
        Assert.That(() => accessor(), Throws.Nothing);
    }
}
