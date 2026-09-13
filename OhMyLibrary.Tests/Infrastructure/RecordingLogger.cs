using Microsoft.Extensions.Logging;

namespace OhMyLibrary.Tests.Infrastructure;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that keeps what it was told.
/// </summary>
/// <remarks>
/// Used where "swallowed" has to be distinguished from "silently ignored": a step that fails and is
/// skipped is only acceptable because it is logged, so the test asserts the log entry as part of the
/// behaviour.
/// </remarks>
/// <typeparam name="T">Log category.</typeparam>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock _sync = new();
    private readonly List<LogEntry> _entries = [];

    /// <summary>Everything logged so far, oldest first.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (_sync)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    /// <summary>One captured log entry.</summary>
    /// <param name="Level">The level it was logged at.</param>
    /// <param name="Message">The formatted message.</param>
    /// <param name="Exception">The exception attached to it, when there was one.</param>
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
