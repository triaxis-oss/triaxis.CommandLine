namespace triaxis.CommandLine.Serilog.Tests;

using global::Serilog.Core;
using global::Serilog.Events;
using global::Serilog.Parsing;

[TestFixture]
public class ShortContextEnricherTests
{
    private static LogEvent CreateEventWithSourceContext(string? sourceContext)
    {
        var props = new List<LogEventProperty>();
        if (sourceContext is not null)
        {
            props.Add(new LogEventProperty("SourceContext", new ScalarValue(sourceContext)));
        }

        return new LogEvent(
            DateTimeOffset.Now,
            LogEventLevel.Information,
            null,
            new MessageTemplate("msg", []),
            props);
    }

    private static string? GetShortContext(LogEvent logEvent)
    {
        return logEvent.Properties.TryGetValue("ShortContext", out var val) && val is ScalarValue sv
            ? sv.Value as string
            : null;
    }

    [Test]
    public void Enrich_DottedSourceContext_YieldsLastSegment()
    {
        var enricher = new ShortContextEnricher();
        var evt = CreateEventWithSourceContext("Namespace.Sub.MyClass");
        enricher.Enrich(evt, new PropertyFactory());
        Assert.That(GetShortContext(evt), Is.EqualTo("MyClass"));
    }

    [Test]
    public void Enrich_NoDot_YieldsOriginalName()
    {
        var enricher = new ShortContextEnricher();
        var evt = CreateEventWithSourceContext("MyClass");
        enricher.Enrich(evt, new PropertyFactory());
        Assert.That(GetShortContext(evt), Is.EqualTo("MyClass"));
    }

    [Test]
    public void Enrich_NoSourceContext_DoesNotAddShortContext()
    {
        var enricher = new ShortContextEnricher();
        var evt = CreateEventWithSourceContext(null);
        enricher.Enrich(evt, new PropertyFactory());
        Assert.That(GetShortContext(evt), Is.Null);
    }

    [Test]
    public void Enrich_IsIdempotent_DoesNotOverwriteExisting()
    {
        var enricher = new ShortContextEnricher();
        var evt = new LogEvent(
            DateTimeOffset.Now,
            LogEventLevel.Information,
            null,
            new MessageTemplate("msg", []),
            [
                new LogEventProperty("SourceContext", new ScalarValue("Foo.Bar.Baz")),
                new LogEventProperty("ShortContext", new ScalarValue("Existing")),
            ]);
        enricher.Enrich(evt, new PropertyFactory());
        Assert.That(GetShortContext(evt), Is.EqualTo("Existing"));
    }

    private class PropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
