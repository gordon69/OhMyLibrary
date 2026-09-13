namespace OhMyLibrary.App.Services;

/// <summary>
/// Owns the library's background sync: the first-run population, the file watcher that keeps it
/// current, and the rescan the shell asks for when the window is activated.
/// </summary>
/// <remarks>
/// Everything here is fire-and-forget by design. A caller on the dispatcher must never wait on a
/// disk or network refresh, so the only member is a request that returns immediately and is
/// coalesced with whatever is already running.
/// </remarks>
public interface ILibrarySyncCoordinator
{
    /// <summary>
    /// Asks for a rescan of the local <c>.acf</c> manifests and returns without waiting for it.
    /// </summary>
    /// <param name="force">
    /// <see langword="true"/> to bypass <c>Sync:LocalRescanCooldownSeconds</c>, which is right for a
    /// watcher event — Steam has just rewritten a manifest and the card should flip in seconds.
    /// Window activation passes <see langword="false"/> so the cooldown keeps focus thrash off the
    /// disk.
    /// </param>
    /// <remarks>
    /// A request that arrives before the implementation's own startup sync has reached its first
    /// scan is folded into that scan rather than starting a competing one, so the first scan of a
    /// session happens exactly once and always on the same path. The shell's window is up before the
    /// sync is queued, so its first activation is exactly that case.
    /// </remarks>
    void RequestLocalRescan(bool force);
}
