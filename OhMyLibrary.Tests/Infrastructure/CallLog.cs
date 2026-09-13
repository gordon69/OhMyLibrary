namespace OhMyLibrary.Tests.Infrastructure;

/// <summary>
/// A thread-safe ordered record of what the fakes were asked to do.
/// </summary>
/// <remarks>
/// Several fakes share one instance, which is the only way to assert on the order of calls made
/// across different collaborators — "the watcher was armed before the coordinator's own refresh
/// steps" is exactly that kind of claim. It says nothing about what the rest of the process is
/// doing at the time, and no assertion written against it should pretend otherwise.
/// </remarks>
public sealed class CallLog
{
    private readonly Lock _sync = new();
    private readonly List<string> _entries = [];

    /// <summary>Everything recorded so far, oldest first.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Appends one entry.</summary>
    /// <param name="entry">A short, stable label for the call.</param>
    public void Record(string entry)
    {
        lock (_sync)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>The position of an entry, or <c>-1</c> when it was never recorded.</summary>
    /// <param name="entry">The label to look for.</param>
    public int IndexOf(string entry)
    {
        lock (_sync)
        {
            return _entries.IndexOf(entry);
        }
    }

    /// <summary>Whether an entry was ever recorded.</summary>
    /// <param name="entry">The label to look for.</param>
    public bool Contains(string entry) => IndexOf(entry) >= 0;

    /// <summary>The log as one line, for an assertion message.</summary>
    public override string ToString() => string.Join(" -> ", Entries);
}
