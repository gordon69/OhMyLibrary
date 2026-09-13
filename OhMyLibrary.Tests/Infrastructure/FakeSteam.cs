using System.Text.RegularExpressions;

namespace OhMyLibrary.Tests.Infrastructure;

/// <summary>
/// A throwaway Steam installation on disk: a root with <c>config</c>, <c>steamapps</c> and
/// <c>appcache</c>, plus any number of extra library folders.
/// </summary>
/// <remarks>
/// <para>
/// The readers resolve and existence-check every path they are handed, so the fixtures cannot carry
/// absolute paths of their own. <c>libraryfolders.vdf</c> is therefore checked in with
/// <c>__LIB0__</c>-style tokens which <see cref="WriteLibraryFolders"/> replaces with the real temp
/// paths, escaped the way Valve escapes them.
/// </para>
/// <para>
/// Nothing here ever touches a real Steam folder: the whole tree lives under the system temp
/// directory and is deleted on dispose.
/// </para>
/// </remarks>
public sealed partial class FakeSteam : IDisposable
{
    private readonly TempDirectory _temp;

    /// <summary>Creates the tree with an empty Steam root.</summary>
    /// <param name="prefix">Short label that appears in the temp folder name.</param>
    public FakeSteam(string prefix = "steam")
    {
        _temp = new TempDirectory(prefix);
        Root = _temp.CreateSubdirectory("Steam");
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(SteamAppsDirectory);
        Directory.CreateDirectory(AppCacheDirectory);
    }

    /// <summary>Absolute path of the Steam install root.</summary>
    public string Root { get; }

    /// <summary>Absolute path of <c>&lt;steam&gt;/config</c>.</summary>
    public string ConfigDirectory => Path.Combine(Root, "config");

    /// <summary>Absolute path of <c>&lt;steam&gt;/steamapps</c>.</summary>
    public string SteamAppsDirectory => Path.Combine(Root, "steamapps");

    /// <summary>Absolute path of <c>&lt;steam&gt;/appcache</c>.</summary>
    public string AppCacheDirectory => Path.Combine(Root, "appcache");

    /// <summary>Absolute path of <c>&lt;steam&gt;/appcache/librarycache</c>.</summary>
    public string LibraryCacheDirectory => Path.Combine(AppCacheDirectory, "librarycache");

    /// <summary>A path inside the temp tree that deliberately does not exist.</summary>
    public string UnreachablePath => _temp.Combine("UnpluggedDrive");

    /// <summary>
    /// Creates an additional library folder beside the Steam root, with its own <c>steamapps</c>.
    /// </summary>
    /// <param name="name">Folder name, for example <c>SteamLibrary</c>.</param>
    /// <returns>The absolute path of the library root, which <i>contains</i> <c>steamapps</c>.</returns>
    public string AddLibrary(string name)
    {
        var library = _temp.CreateSubdirectory(name);
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        return library;
    }

    /// <summary>
    /// Writes a <c>libraryfolders.vdf</c> fixture, replacing its path tokens.
    /// </summary>
    /// <param name="fixtureName">File name under <c>Fixtures/LibraryFolders</c>.</param>
    /// <param name="substitutions">Token (such as <c>__LIB0__</c>) to absolute path.</param>
    /// <param name="location">Which of the two locations Steam uses to write the file.</param>
    /// <returns>The absolute path of the file that was written.</returns>
    public string WriteLibraryFolders(
        string fixtureName,
        IReadOnlyDictionary<string, string> substitutions,
        LibraryFoldersLocation location = LibraryFoldersLocation.SteamApps)
    {
        var content = Fixture.ReadText("LibraryFolders", fixtureName);
        foreach (var (token, path) in substitutions)
        {
            content = content.Replace(token, Escape(path), StringComparison.Ordinal);
        }

        var directory = location == LibraryFoldersLocation.SteamApps ? SteamAppsDirectory : ConfigDirectory;
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, "libraryfolders.vdf");
        File.WriteAllText(file, content);
        return file;
    }

    /// <summary>
    /// Writes a <c>config/loginusers.vdf</c> fixture verbatim.
    /// </summary>
    /// <param name="fixtureName">File name under <c>Fixtures/LoginUsers</c>.</param>
    /// <returns>The absolute path of the file that was written.</returns>
    public string WriteLoginUsers(string fixtureName)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var file = Path.Combine(ConfigDirectory, "loginusers.vdf");
        File.Copy(Fixture.Resolve("LoginUsers", fixtureName), file, overwrite: true);
        return file;
    }

    /// <summary>
    /// Copies an <c>appmanifest_*.acf</c> fixture into a library's <c>steamapps</c> folder.
    /// </summary>
    /// <param name="libraryPath">Library root, from <see cref="AddLibrary"/> or <see cref="Root"/>.</param>
    /// <param name="fixtureName">File name under <c>Fixtures/Acf</c>.</param>
    /// <param name="createInstallDirectory">
    /// Whether to create <c>steamapps/common/&lt;installdir&gt;</c> as well. Leave it
    /// <see langword="false"/> to reproduce a stale manifest, whose install path cannot be resolved.
    /// </param>
    /// <returns>The absolute path of the manifest that was written.</returns>
    public string InstallManifest(string libraryPath, string fixtureName, bool createInstallDirectory = true)
    {
        var source = Fixture.Resolve("Acf", fixtureName);
        var steamApps = Path.Combine(libraryPath, "steamapps");
        Directory.CreateDirectory(steamApps);

        var manifest = Path.Combine(steamApps, fixtureName);
        File.Copy(source, manifest, overwrite: true);

        if (createInstallDirectory && InstallDirOf(File.ReadAllText(source)) is { Length: > 0 } installDir)
        {
            Directory.CreateDirectory(Path.Combine(steamApps, "common", installDir));
        }

        return manifest;
    }

    /// <summary>
    /// Creates a file inside the library art cache for an app.
    /// </summary>
    /// <param name="appId">Steam application id.</param>
    /// <param name="relativePath">
    /// Path below <c>librarycache/&lt;appid&gt;/</c>, either a loose file name or one inside a hash
    /// subfolder such as <c>6843027380c3bfd0952449fd9174f492ef2e7b40/library_capsule.jpg</c>.
    /// </param>
    /// <returns>The absolute path of the file that was written.</returns>
    public string WriteLibraryArt(int appId, string relativePath)
    {
        var file = Path.Combine(LibraryCacheDirectory, appId.ToString(), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [0xFF, 0xD8, 0xFF, 0xD9]);
        return file;
    }

    /// <summary>Writes a file anywhere inside the tree, creating the directories above it.</summary>
    /// <param name="relativePath">Path below the temp root, not below the Steam root.</param>
    /// <param name="content">File content.</param>
    /// <returns>The absolute path of the file that was written.</returns>
    public string WriteFile(string relativePath, byte[] content) => _temp.WriteFile(relativePath, content);

    /// <summary>Deletes the whole tree.</summary>
    public void Dispose() => _temp.Dispose();

    /// <summary>Escapes a Windows path the way Valve writes it into a text VDF.</summary>
    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string? InstallDirOf(string manifest)
    {
        var match = InstallDirPattern().Match(manifest);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""^\s*"installdir"\s*"([^"]*)"\s*$""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirPattern();
}

/// <summary>Which of the two paths Steam writes <c>libraryfolders.vdf</c> to.</summary>
public enum LibraryFoldersLocation
{
    /// <summary>The canonical <c>&lt;steam&gt;/steamapps/libraryfolders.vdf</c>.</summary>
    SteamApps,

    /// <summary>The legacy <c>&lt;steam&gt;/config/libraryfolders.vdf</c>.</summary>
    Config,
}
