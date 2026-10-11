using Microsoft.Extensions.Logging;

namespace TuPago3.Sdk.Tests.TestInfra;

/// <summary>Captures all log output so tests can assert that secrets (JWTs) never appear.</summary>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly List<string> _messages = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Messages
    {
        get { lock (_gate) return _messages.ToArray(); }
    }

    public void Clear()
    {
        lock (_gate) _messages.Clear();
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private void Record(string message)
    {
        lock (_gate) _messages.Add(message);
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerFactory _owner;
        public CapturingLogger(CapturingLoggerFactory owner) => _owner = owner;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _owner.Record(formatter(state, exception));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
