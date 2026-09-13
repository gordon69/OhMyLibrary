namespace OhMyLibrary.Tests.Infrastructure;

/// <summary>
/// A counting signal a test can wait on: "block until this has happened at least <c>N</c> times".
/// </summary>
/// <remarks>
/// This is what the coordinator tests use instead of sleeping. Every wait is a wait for something
/// the code under test actually did, so the tests are driven by the production code's own progress
/// rather than by a race against <see cref="Task.Delay(TimeSpan)"/>. The timeout is generous and
/// exists only so a deadlock fails the test instead of hanging the run.
/// </remarks>
public sealed class CountingSignal
{
    /// <summary>How long a wait may take before the test is declared hung.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _sync = new();
    private readonly List<(int Target, TaskCompletionSource Completion)> _waiters = [];
    private readonly string _name;

    private int _count;

    /// <summary>Creates the signal.</summary>
    /// <param name="name">What the signal stands for; it appears in the timeout message.</param>
    public CountingSignal(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>How many times the signal has been raised.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>Records one occurrence and releases everything waiting for it.</summary>
    public void Raise()
    {
        List<TaskCompletionSource> ready = [];

        lock (_sync)
        {
            _count++;

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Target <= _count)
                {
                    ready.Add(_waiters[i].Completion);
                    _waiters.RemoveAt(i);
                }
            }
        }

        // Outside the lock: a continuation must never run with it held.
        foreach (var completion in ready)
        {
            completion.SetResult();
        }
    }

    /// <summary>Waits until the signal has been raised at least <paramref name="target"/> times.</summary>
    /// <param name="target">The count to wait for; occurrences that already happened count.</param>
    /// <exception cref="TimeoutException">The count was not reached within <see cref="DefaultTimeout"/>.</exception>
    public Task WaitAsync(int target = 1) => WaitAsync(target, DefaultTimeout);

    /// <summary>Waits until the signal has been raised at least <paramref name="target"/> times.</summary>
    /// <param name="target">The count to wait for; occurrences that already happened count.</param>
    /// <param name="timeout">How long to wait before declaring the test hung.</param>
    /// <exception cref="TimeoutException">The count was not reached in time.</exception>
    public async Task WaitAsync(int target, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(target, 1);

        TaskCompletionSource completion;

        lock (_sync)
        {
            if (_count >= target)
            {
                return;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, completion));
        }

        try
        {
            await completion.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Timed out waiting for '{_name}' to be raised {target} time(s); it was raised {Count} time(s).");
        }
    }
}
