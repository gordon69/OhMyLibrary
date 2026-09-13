using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Parses <c>steamapps/appmanifest_&lt;appid&gt;.acf</c> files.
/// </summary>
/// <remarks>
/// <para>
/// Reading is synchronous on purpose: these files are a few kilobytes each and are read in bulk.
/// A malformed or vanished manifest yields <see langword="null"/> or is skipped, never an exception.
/// </para>
/// <para>
/// The bulk overloads take a <see cref="CancellationToken"/> because a full scan is thousands of
/// file reads on a large install and a caller running one on a thread pool thread cannot otherwise
/// stop it: wrapping the call in <see cref="Task.Run(Action, CancellationToken)"/> only stops it
/// from <i>starting</i>. Implementations check the token once per manifest, which is the granularity
/// that makes shutdown a matter of milliseconds instead of a whole scan.
/// </para>
/// </remarks>
public interface IAcfReader
{
    /// <summary>
    /// Parses a single manifest.
    /// </summary>
    /// <param name="manifestPath">Absolute path of the <c>.acf</c> file.</param>
    /// <param name="libraryPath">Absolute path of the library root that owns it, used to resolve the install path.</param>
    /// <returns>The parsed app, or <see langword="null"/> when the file is missing or unparseable.</returns>
    InstalledApp? Read(string manifestPath, string libraryPath);

    /// <summary>
    /// Parses every manifest in one library folder. Empty when the folder is unreachable.
    /// </summary>
    /// <param name="folder">The library folder to enumerate.</param>
    /// <param name="ct">
    /// Cancellation token, observed once per manifest. Cancelling throws
    /// <see cref="OperationCanceledException"/> rather than returning a partial list, so a caller
    /// cannot mistake an abandoned scan for an empty library folder.
    /// </param>
    IReadOnlyList<InstalledApp> ReadLibrary(SteamLibraryFolder folder, CancellationToken ct = default);

    /// <summary>
    /// Parses every manifest across several library folders. Later folders win when the same app id
    /// appears twice, which happens after a library move.
    /// </summary>
    /// <param name="folders">The library folders to enumerate.</param>
    /// <param name="ct">
    /// Cancellation token, observed once per manifest. Cancelling throws
    /// <see cref="OperationCanceledException"/> rather than returning a partial list, so a caller
    /// cannot mistake an abandoned scan for a machine with nothing installed.
    /// </param>
    IReadOnlyList<InstalledApp> ReadAll(IEnumerable<SteamLibraryFolder> folders, CancellationToken ct = default);
}
