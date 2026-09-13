using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Parses the binary <c>appcache/appinfo.vdf</c> container (v28 and v29).
/// </summary>
/// <remarks>
/// <para>
/// The file holds every app the client knows about — a few thousand records — so prefer
/// <see cref="ReadApps"/> when only the installed and owned ids matter. A record that fails to
/// parse is skipped without desyncing the rest of the file.
/// </para>
/// <para>
/// Both methods take a <see cref="CancellationToken"/> because walking the container is a
/// multi-second pass over several megabytes on a large install, and a caller that wrapped the call
/// in <see cref="Task.Run(Action, CancellationToken)"/> would only have stopped it from
/// <i>starting</i>. Implementations check the token once per record, which is the granularity that
/// lets shutdown abandon the parse in milliseconds.
/// </para>
/// </remarks>
public interface IAppInfoReader
{
    /// <summary>
    /// Parses every record. Empty when the file is missing or its magic is not a supported version.
    /// </summary>
    /// <param name="appInfoVdfPath">Absolute path of <c>appinfo.vdf</c>.</param>
    /// <param name="ct">
    /// Cancellation token, observed once per record. Cancelling throws
    /// <see cref="OperationCanceledException"/> rather than returning the records read so far, so an
    /// abandoned parse cannot be mistaken for a file that holds nothing.
    /// </param>
    IReadOnlyList<AppInfoEntry> ReadAll(string appInfoVdfPath, CancellationToken ct = default);

    /// <summary>
    /// Parses only the requested apps, skipping the key-values body of every other record.
    /// </summary>
    /// <param name="appInfoVdfPath">Absolute path of <c>appinfo.vdf</c>.</param>
    /// <param name="appIds">The app ids of interest.</param>
    /// <param name="ct">
    /// Cancellation token, observed once per record. Cancelling throws
    /// <see cref="OperationCanceledException"/> rather than returning the records read so far, so an
    /// abandoned parse cannot be mistaken for a file that holds none of the requested apps.
    /// </param>
    /// <returns>The entries that were found, keyed by app id. Ids absent from the file are simply missing.</returns>
    IReadOnlyDictionary<int, AppInfoEntry> ReadApps(string appInfoVdfPath, IReadOnlySet<int> appIds, CancellationToken ct = default);
}
