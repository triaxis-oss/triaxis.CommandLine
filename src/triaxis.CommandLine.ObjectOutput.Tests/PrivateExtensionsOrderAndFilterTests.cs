namespace triaxis.CommandLine.ObjectOutput.Tests;

using triaxis.Reflection;

[TestFixture]
public class PrivateExtensionsOrderAndFilterTests
{
    private class FakeField : IObjectField, IObjectFieldOrdering
    {
        public string Title { get; set; } = "";
        public string Name { get; set; } = "";
        public ObjectFieldVisibility Visibility { get; set; }
        public Type Type { get; set; } = typeof(string);
        public System.ComponentModel.TypeConverter Converter { get; set; } = new System.ComponentModel.StringConverter();
        public IPropertyGetter Accessor { get; set; } = null!;
        public string? Before { get; set; }
        public string? After { get; set; }
    }

    [Test]
    public void Filter_KeepsOnlyFieldsAtOrBelowMaxVisibility()
    {
        var fields = new[]
        {
            new FakeField { Name = "a", Visibility = ObjectFieldVisibility.Standard },
            new FakeField { Name = "b", Visibility = ObjectFieldVisibility.Extended },
            new FakeField { Name = "c", Visibility = ObjectFieldVisibility.Internal },
        };

        var std = fields.Filter(ObjectFieldVisibility.Standard).ToArray();
        Assert.That(std.Select(f => f.Name), Is.EqualTo(new[] { "a" }));

        var ext = fields.Filter(ObjectFieldVisibility.Extended).ToArray();
        Assert.That(ext.Select(f => f.Name), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void Filter_ReturnsAllIfFilterWouldYieldEmpty()
    {
        var fields = new[]
        {
            new FakeField { Name = "a", Visibility = ObjectFieldVisibility.Extended },
            new FakeField { Name = "b", Visibility = ObjectFieldVisibility.Internal },
        };

        var result = fields.Filter(ObjectFieldVisibility.Standard).ToArray();
        Assert.That(result.Select(f => f.Name), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void Ordered_RespectsBeforeAfterDirectives()
    {
        var fields = new[]
        {
            new FakeField { Name = "A" },
            new FakeField { Name = "B" },
            new FakeField { Name = "Header", Before = "A" },
            new FakeField { Name = "Footer", After = "B" },
        };

        var result = fields.Ordered();
        Assert.That(result.Select(f => f.Name), Is.EqualTo(new[] { "Header", "A", "B", "Footer" }));
    }

    [Test]
    public void Ordered_WithNoOrderingDirectives_PreservesOriginalOrder()
    {
        var fields = new[]
        {
            new FakeField { Name = "X" },
            new FakeField { Name = "Y" },
            new FakeField { Name = "Z" },
        };

        var result = fields.Ordered();
        Assert.That(result.Select(f => f.Name), Is.EqualTo(new[] { "X", "Y", "Z" }));
    }

    [Test]
    public void Ordered_UnmatchedBeforeAfter_StillAppearsInOutput()
    {
        var fields = new[]
        {
            new FakeField { Name = "A" },
            new FakeField { Name = "Orphan", Before = "DoesNotExist" },
        };
        var result = fields.Ordered();
        Assert.That(result.Select(f => f.Name), Does.Contain("Orphan"));
        Assert.That(result.Select(f => f.Name), Does.Contain("A"));
    }
}
