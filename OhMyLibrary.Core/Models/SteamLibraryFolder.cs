namespace OhMyLibrary.Core.Models;

/// <summary>
/// One entry of <c>steamapps/libraryfolders.vdf</c>.
/// </summary>
/// <param name="Path">
/// The library <i>root</i> (for example <c>A:\SteamLibrary</c>), which <b>contains</b> the
/// <c>steamapps</c> folder rather than being it.
/// </param>
/// <param name="Label">User-assigned label; usually empty.</param>
/// <param name="TotalSize">Capacity Steam reported for the volume; <c>0</c> for the primary library.</param>
/// <param name="Apps">
/// Steam's own app id to size-on-disk index. It lags reality — the <c>.acf</c> files are the truth,
/// this is only a hint.
/// </param>
public sealed record SteamLibraryFolder(
    string Path,
    string Label,
    long TotalSize,
    IReadOnlyDictionary<int, long> Apps)
{
    /// <summary>Absolute path of the <c>steamapps</c> folder holding the manifests.</summary>
    public string SteamAppsPath => System.IO.Path.Combine(Path, "steamapps");

    /// <summary>Absolute path of the <c>steamapps/common</c> folder holding the installs.</summary>
    public string CommonPath => System.IO.Path.Combine(SteamAppsPath, "common");
}
