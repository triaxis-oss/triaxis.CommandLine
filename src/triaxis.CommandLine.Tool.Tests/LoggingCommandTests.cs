namespace triaxis.CommandLine.ToolTests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

[TestFixture]
public class LoggingCommandTests
{
    // Exposes the protected Logger/CreateLogger members for testing
    private class TestableLoggingCommand : LoggingCommand
    {
        public ILogger GetLogger() => Logger;
        public ILogger GetNamedLogger(string name) => CreateLogger(name);
    }

    private static T CreateInstance<T>() where T : LoggingCommand, new()
    {
        // Mimic DI injection of the protected _loggerFactory field via reflection
        var instance = new T();
        var field = typeof(LoggingCommand).GetField("_loggerFactory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(instance, NullLoggerFactory.Instance);
        return instance;
    }

    [Test]
    public void Logger_ReturnsANonNullLogger()
    {
        var cmd = CreateInstance<TestableLoggingCommand>();
        var logger = cmd.GetLogger();
        Assert.That(logger, Is.Not.Null);
    }

    [Test]
    public void Logger_IsCached_ReturnsSameInstanceOnRepeatedAccess()
    {
        var cmd = CreateInstance<TestableLoggingCommand>();
        var first = cmd.GetLogger();
        var second = cmd.GetLogger();
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void CreateLogger_WithName_ReturnsNonNullLogger()
    {
        var cmd = CreateInstance<TestableLoggingCommand>();
        var logger = cmd.GetNamedLogger("sub");
        Assert.That(logger, Is.Not.Null);
    }
}
