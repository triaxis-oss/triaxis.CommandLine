namespace triaxis.CommandLine.ObjectOutput.Tests;

using triaxis.Reflection;

[TestFixture]
public class ObjectDescriptorRegistryTests
{
    public record Registered(string Only);
    public record NotRegistered(string Name, int Count);

    private sealed class StubField : IObjectField, IPropertyGetter
    {
        public string Name => "Stub";
        public string Title => "Stub";
        public ObjectFieldVisibility Visibility => ObjectFieldVisibility.Standard;
        public Type Type => typeof(string);
        public string? Format => null;
        public IPropertyGetter Accessor => this;
        public System.Reflection.PropertyInfo Property => throw new NotSupportedException();
        public object Get(object target) => "stubbed";
    }

    private sealed class StubDescriptor : IObjectDescriptor
    {
        public IReadOnlyList<IObjectField> Fields { get; } = [new StubField()];
    }

    [OneTimeSetUp]
    public void Register()
        => ObjectDescriptorRegistry.Register(t => t == typeof(Registered) ? new StubDescriptor() : null);

    [Test]
    public void RegisteredDescriptorWinsOverReflection()
    {
        var descriptor = new DefaultObjectDescriptorProvider<Registered>().GetDescriptor(new("x"));

        Assert.That(descriptor, Is.TypeOf<StubDescriptor>());
        Assert.That(descriptor.Fields.Select(f => f.Name), Is.EqualTo(new[] { "Stub" }));
    }

    [Test]
    public void UnregisteredTypeFallsBackToReflection()
    {
        var descriptor = new DefaultObjectDescriptorProvider<NotRegistered>().GetDescriptor(new("x", 1));

        Assert.That(descriptor.Fields.Select(f => f.Name), Is.EqualTo(new[] { "Name", "Count" }));
    }

    [Test]
    public void NestedLookupAlsoConsultsTheRegistry()
    {
        Assert.That(RuntimeObjectDescriptor.For(typeof(Registered)), Is.TypeOf<StubDescriptor>());
    }

    [Test]
    public void FallbackDisabled_ThrowsForAnUnregisteredType()
    {
        AppContext.SetSwitch(ObjectOutputFeatures.ReflectionFallbackSwitch, false);
        try
        {
            Assert.That(ObjectOutputFeatures.ReflectionFallbackEnabled, Is.False);
            Assert.That(() => RuntimeObjectDescriptor.For(typeof(NotRegistered)),
                Throws.TypeOf<NotSupportedException>());
            // a registered type still resolves, which is what makes the switch safe to set
            Assert.That(RuntimeObjectDescriptor.For(typeof(Registered)), Is.TypeOf<StubDescriptor>());
        }
        finally
        {
            AppContext.SetSwitch(ObjectOutputFeatures.ReflectionFallbackSwitch, true);
        }
    }
}
