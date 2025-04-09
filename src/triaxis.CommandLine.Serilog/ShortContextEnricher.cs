using Serilog.Core;
using Serilog.Events;

namespace triaxis.CommandLine;

class ShortContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var sourceContext) &&
            sourceContext is ScalarValue contextScalar &&
            contextScalar.Value is string context)
        {
            var lastDot = context.LastIndexOf('.');
            if (lastDot >= 0)
            {
                context = context.Substring(lastDot + 1);
            }
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ShortContext", context));
        }
    }
}
