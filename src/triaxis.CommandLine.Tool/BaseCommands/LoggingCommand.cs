namespace triaxis.CommandLine;

using Microsoft.Extensions.Logging;

public class LoggingCommand
{
    [Inject]
    protected readonly ILoggerFactory _loggerFactory = null!;

    protected ILogger Logger => _scopeLogger.Value ?? (_logger ??= CreateLogger());

    private ILogger? _logger;
    private static readonly AsyncLocal<ILogger?> _scopeLogger = new();

    protected ILogger CreateLogger(string? name = null)
        => name is null ? _loggerFactory.CreateLogger(GetType()) : _loggerFactory.CreateLogger($"{GetType()}.{name}");

    protected static IDisposable LoggerScope(ILogger logger)
    {
        var prev = new RestoreScope(_scopeLogger.Value);
        _scopeLogger.Value = logger;
        return prev;
    }

    class RestoreScope : IDisposable
    {
        private readonly ILogger? _logger;

        public RestoreScope(ILogger? logger)
        {
            _logger = logger;
        }

        public void Dispose()
        {
            _scopeLogger.Value = _logger;
        }
    }
}
