using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Core.Abstractions;

/// <summary>
/// Reads <c>libraryfolders.vdf</c>, preferring <c>&lt;steam&gt;/steamapps/</c> and falling back to
/// <c>&lt;steam&gt;/config/</c>.
/// </summary>
public interface ILibraryFoldersReader
{
    /// <summary>
    /// Reads the declared library folders. Returns an empty list when neither file exists or the
    /// file cannot be parsed; the caller is responsible for the existence check on each folder.
    /// </summary>
    /// <param name="steamPath">Absolute Steam install root.</param>
    IReadOnlyList<SteamLibraryFolder> Read(string steamPath);
}
