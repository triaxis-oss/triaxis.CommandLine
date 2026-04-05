namespace triaxis.CommandLine.ObjectOutput.Tests;

using System.Data;
using System.Linq;

[TestFixture]
public class DefaultObjectDescriptorProviderTests
{
    public record Person(string Name, int Age)
    {
        [ObjectOutput(ObjectFieldVisibility.Extended)]
        public int AgeInMonths => Age * 12;
    }

    public record OrderedFields(string A, string B)
    {
        [ObjectOutput(Before = nameof(A))]
        public string Header => "head";
        [ObjectOutput(After = nameof(B))]
        public string Footer => "foot";
    }

    [Test]
    public void GetDescriptor_ForSimpleType_EnumeratesFieldsInDeclarationOrder()
    {
        var provider = new DefaultObjectDescriptorProvider<Person>();
        var descriptor = provider.GetDescriptor(new Person("Alice", 30));
        var names = descriptor.Fields.Select(f => f.Name).ToArray();
        Assert.That(names, Does.Contain("Name"));
        Assert.That(names, Does.Contain("Age"));
        Assert.That(names, Does.Contain("AgeInMonths"));
        // Declaration order: positional Name, Age come first; AgeInMonths last
        Assert.That(Array.IndexOf(names, "Name"), Is.LessThan(Array.IndexOf(names, "AgeInMonths")));
    }

    [Test]
    public void SimpleDescriptor_FieldVisibilityFromAttribute()
    {
        var provider = new DefaultObjectDescriptorProvider<Person>();
        var descriptor = provider.GetDescriptor(new Person("Alice", 30));
        var extMonths = descriptor.Fields.First(f => f.Name == "AgeInMonths");
        Assert.That(extMonths.Visibility, Is.EqualTo(ObjectFieldVisibility.Extended));
    }

    [Test]
    public void SimpleDescriptor_FieldAccessor_ReturnsValue()
    {
        var provider = new DefaultObjectDescriptorProvider<Person>();
        var person = new Person("Alice", 30);
        var descriptor = provider.GetDescriptor(person);
        var nameField = descriptor.Fields.First(f => f.Name == "Name");
        Assert.That(nameField.Accessor.Get(person), Is.EqualTo("Alice"));
    }

    [Test]
    public void OrderedFields_BeforeAfterAttributes_AffectOrdering()
    {
        var provider = new DefaultObjectDescriptorProvider<OrderedFields>();
        var descriptor = provider.GetDescriptor(new OrderedFields("a", "b"));
        var names = descriptor.Fields.Select(f => f.Name).ToArray();
        // Expected: Header, A, B, Footer (Header before A, Footer after B)
        var iHeader = Array.IndexOf(names, "Header");
        var iA = Array.IndexOf(names, "A");
        var iB = Array.IndexOf(names, "B");
        var iFooter = Array.IndexOf(names, "Footer");
        Assert.That(iHeader, Is.LessThan(iA));
        Assert.That(iA, Is.LessThan(iB));
        Assert.That(iB, Is.LessThan(iFooter));
    }

    [Test]
    public void GetDescriptor_ForDataTable_ReturnsDataTableDescriptor()
    {
        var provider = new DefaultObjectDescriptorProvider<DataTable>();
        var dt = new DataTable();
        dt.Columns.Add("Col1", typeof(string));
        dt.Columns.Add("Col2", typeof(int));
        dt.Rows.Add("x", 1);
        var descriptor = provider.GetDescriptor(dt);
        var names = descriptor.Fields.Select(f => f.Name).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "Col1", "Col2" }));
    }

#if !NETFRAMEWORK
    // TupleObjectDescriptor is only compiled on netstandard2.1+ (see TupleObjectDescriptor.cs).
    [Test]
    public void GetDescriptor_ForTuple_ReturnsTupleDescriptor()
    {
        var provider = new DefaultObjectDescriptorProvider<(Person Main, string Extra)>();
        var descriptor = provider.GetDescriptor((new Person("A", 1), "extra"));
        Assert.That(descriptor, Is.Not.Null);
        Assert.That(descriptor.Fields, Is.Not.Empty);
    }
#endif
}
