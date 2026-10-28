using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Overseer.Tests;

/// <summary>Every log entry a server writes, as text: the formatted message followed by any exception.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? "" : "\n" + exception)}");
    }
}
