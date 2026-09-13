using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;

namespace OhMyLibrary.App.ViewModels;

/// <summary>
/// Base class for every page and item view model: observable, with a busy indicator and a single
/// place where service exceptions turn into a message instead of a crash.
/// </summary>
/// <remarks>
/// <para>
/// Core's services promise not to throw for a degraded environment, but a parse bug on somebody's
/// machine is not a degraded environment. <see cref="RunGuardedAsync"/> is the net: every command
/// body goes through it, so the worst outcome a user sees is a red line of text and a log entry.
/// </para>
/// <para>
/// The guard touches observable properties, so call it from the dispatcher thread. Overlapping calls
/// are allowed and reference-counted: <see cref="IsBusy"/> stays <see langword="true"/> until the
/// last one finishes.
/// </para>
/// </remarks>
/// <param name="logger">Log sink for guarded failures.</param>
public abstract partial class ViewModelBase(ILogger logger) : ObservableObject
{
    private int _busyCount;

    /// <summary>Log sink available to derived view models.</summary>
    protected ILogger Logger { get; } = logger;

    /// <summary>True while at least one guarded operation is running.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>What the busy operation is doing, for a progress ring caption.</summary>
    [ObservableProperty]
    private string? _busyMessage;

    /// <summary>
    /// The last guarded failure, or <see langword="null"/>. Bind an error bar to this and to
    /// <see cref="HasError"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>True when <see cref="ErrorMessage"/> holds something worth showing.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Dismisses the current error message.</summary>
    public void ClearError() => ErrorMessage = null;

    /// <summary>
    /// Runs an operation with the busy indicator set, turning any failure into
    /// <see cref="ErrorMessage"/> plus a log entry rather than an unhandled exception.
    /// </summary>
    /// <param name="operation">The work to run. It receives <paramref name="ct"/>.</param>
    /// <param name="busyMessage">Caption shown while the work runs, for example "Loading library".</param>
    /// <param name="ct">
    /// Cancellation token. A cancellation requested through this token is treated as a normal
    /// outcome and leaves no error message.
    /// </param>
    protected async Task RunGuardedAsync(
        Func<CancellationToken, Task> operation,
        string busyMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        ErrorMessage = null;
        BusyMessage = busyMessage;

        if (Interlocked.Increment(ref _busyCount) == 1)
        {
            IsBusy = true;
        }

        try
        {
            await operation(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogDebug("{ViewModel} cancelled: {Operation}", GetType().Name, busyMessage);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{ViewModel} failed: {Operation}", GetType().Name, busyMessage);
            ErrorMessage = Describe(ex);
        }
        finally
        {
            if (Interlocked.Decrement(ref _busyCount) == 0)
            {
                IsBusy = false;
                BusyMessage = null;
            }
        }
    }

    /// <summary>
    /// Turns an exception into one line a user can act on. Override to add context specific to a page.
    /// </summary>
    /// <param name="exception">The exception that was caught.</param>
    protected virtual string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is AggregateException aggregate && aggregate.InnerException is { } inner
            ? inner.Message
            : exception.Message;
    }
}
