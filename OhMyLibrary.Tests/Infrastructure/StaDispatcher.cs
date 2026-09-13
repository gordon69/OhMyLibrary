using System.Windows.Threading;

namespace OhMyLibrary.Tests.Infrastructure;

/// <summary>
/// A dedicated STA thread running a real WPF <see cref="Dispatcher"/>, so a test can call code the
/// way the shell's UI thread calls it.
/// </summary>
/// <remarks>
/// <para>
/// xUnit runs its tests on MTA thread-pool threads, and WPF types refuse to be created on one. The
/// shell's <c>MainWindow.Activated</c> handler runs on the dispatcher, so a test that wants to prove
/// something about what that handler may or may not wait behind has to run on a dispatcher too —
/// hence a real one here rather than a plain <see cref="Thread"/> with the apartment flag flipped.
/// </para>
/// <para>
/// The alternative, a custom <c>[StaFact]</c> attribute, means an xUnit test-case discoverer and a
/// serialisable test case for what is currently one test; this is the same guarantee with none of
/// that machinery, and it composes with <c>async</c> test methods.
/// </para>
/// </remarks>
public sealed class StaDispatcher : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    private bool _disposed;

    private StaDispatcher(Thread thread, Dispatcher dispatcher)
    {
        _thread = thread;
        _dispatcher = dispatcher;
    }

    /// <summary>Starts the thread and waits until its dispatcher is pumping.</summary>
    /// <param name="name">Thread name, so a leftover is identifiable in a debugger.</param>
    public static StaDispatcher Start(string name = "oml-test-sta")
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            Name = name,
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return new StaDispatcher(thread, ready.Task.GetAwaiter().GetResult());
    }

    /// <summary>The apartment state of the dispatcher thread, for a test that wants to assert on it.</summary>
    public ApartmentState ApartmentState => _thread.GetApartmentState();

    /// <summary>Runs <paramref name="action"/> on the dispatcher thread.</summary>
    /// <param name="action">The work; exceptions surface on the returned task.</param>
    /// <returns>A task that completes when the action has run.</returns>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>Shuts the dispatcher down and waits for the thread to end.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _dispatcher.InvokeShutdown();
        _ = _thread.Join(TimeSpan.FromSeconds(10));
    }
}
