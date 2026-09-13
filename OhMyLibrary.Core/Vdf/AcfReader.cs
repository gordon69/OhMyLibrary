using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using Serilog;
using Serilog.Core;

namespace OhMyLibrary.Core.Vdf;

/// <summary>
/// Reads <c>steamapps/appmanifest_&lt;appid&gt;.acf</c> — Steam's own record of what is installed
/// on a given library folder.
/// </summary>
/// <remarks>
/// <para>
/// The manifests are the truth about installs; <c>libraryfolders.vdf</c>'s <c>apps</c> block is only
/// a hint. They are also rewritten constantly (every progress tick of a download rewrites one), so
/// a locked or half-written file is an ordinary event: it is logged at warning level and skipped,
/// and the next scan picks the app up again.
/// </para>
/// <para>
/// Non-games live here too — <c>228980</c> "Steamworks Common Redistributables" is the obvious one.
/// Filtering them out needs <c>common/type</c> from <c>appinfo.vdf</c> and is deliberately not done
/// at this level.
/// </para>
/// </remarks>
public sealed class AcfReader : IAcfReader
{
    private const string RootKey = "AppState";
    private const string SteamAppsFolderName = "steamapps";
    private const string CommonFolderName = "common";
    private const string ManifestSearchPattern = "appmanifest_*.acf";
    private const string FileKind = "app manifest";

    private readonly ILogger _logger;

    /// <summary>Creates a reader.</summary>
    /// <param name="logger">
    /// Logger for skipped manifests. <see langword="null"/> is accepted and silences the reader,
    /// which keeps it usable from tests without a logging pipeline.
    /// </param>
    public AcfReader(ILogger? logger = null) => _logger = (logger ?? Logger.None).ForContext<AcfReader>();

    /// <inheritdoc />
    public InstalledApp? Read(string manifestPath, string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return null;
        }

        var document = VdfFile.TryLoadText(manifestPath, _logger, FileKind);
        if (document is null)
        {
            return null;
        }

        if (!string.Equals(document.Name, RootKey, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning("Skipping {Path}: root key is {RootKey}, expected {Expected}", manifestPath, document.Name, RootKey);
            return null;
        }

        var state = document.Root;
        if (!state.TryGetInt("appid", out var appId) || appId <= 0)
        {
            _logger.Warning("Skipping {Path}: no usable appid", manifestPath);
            return null;
        }

        var library = Normalise(libraryPath);
        var installDir = state.GetString("installdir")?.Trim() ?? string.Empty;

        var name = state.GetString("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = installDir.Length > 0 ? installDir : $"App {appId}";
        }

        return new InstalledApp(
            AppId: appId,
            Name: name,
            StateFlags: (AppStateFlags)state.GetInt("StateFlags"),
            InstallDir: installDir,
            FullInstallPath: ResolveInstallPath(library, installDir),
            ManifestPath: Normalise(manifestPath),
            LibraryPath: library,
            SizeOnDisk: state.GetLong("SizeOnDisk"),
            BytesDownloaded: state.GetLong("BytesDownloaded"),
            BytesToDownload: state.GetLong("BytesToDownload"),
            StagingSize: state.GetLong("StagingSize"),
            BuildId: state.GetString("buildid"),
            TargetBuildId: state.GetString("TargetBuildID"),
            LastOwner: state.GetULong("LastOwner"),
            LastUpdated: state.GetUnixTime("lastupdated"),
            LastPlayed: state.GetUnixTime("LastPlayed"));
    }

    /// <inheritdoc />
    public IReadOnlyList<InstalledApp> ReadLibrary(SteamLibraryFolder folder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ct.ThrowIfCancellationRequested();

        var steamApps = folder.SteamAppsPath;
        if (!Directory.Exists(steamApps))
        {
            _logger.Warning("Skipping library {Library}: {SteamApps} is not reachable", folder.Path, steamApps);
            return [];
        }

        string[] manifests;
        try
        {
            manifests = Directory.GetFiles(steamApps, ManifestSearchPattern);
        }
        catch (Exception ex) when (VdfFile.IsExpected(ex))
        {
            _logger.Warning(ex, "Skipping library {Library}: {Reason}", folder.Path, ex.Message);
            return [];
        }

        var apps = new List<InstalledApp>(manifests.Length);
        foreach (var manifest in manifests)
        {
            // Once per manifest. A library with 20 000 installed apps is minutes of file reads, and
            // a token checked only before the loop would let shutdown wait out every one of them.
            ct.ThrowIfCancellationRequested();

            var app = Read(manifest, folder.Path);
            if (app is not null)
            {
                apps.Add(app);
            }
        }

        return apps;
    }

    /// <inheritdoc />
    public IReadOnlyList<InstalledApp> ReadAll(IEnumerable<SteamLibraryFolder> folders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var byAppId = new Dictionary<int, InstalledApp>();
        foreach (var folder in folders)
        {
            foreach (var app in ReadLibrary(folder, ct))
            {
                byAppId[app.AppId] = app;
            }
        }

        return [.. byAppId.Values];
    }

    /// <summary>
    /// Turns the <c>installdir</c> folder <i>name</i> into an absolute path under the owning
    /// library, or <see langword="null"/> when nothing is actually there — a stale manifest for an
    /// interrupted install, or a library on a drive that is no longer mounted.
    /// </summary>
    private static string? ResolveInstallPath(string libraryPath, string installDir)
    {
        if (libraryPath.Length == 0 || installDir.Length == 0)
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(libraryPath, SteamAppsFolderName, CommonFolderName, installDir));
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (VdfFile.IsExpected(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Normalises a path Steam may have written with forward slashes or in the wrong case, keeping
    /// the original text when it is too malformed for <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    private static string Normalise(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception ex) when (VdfFile.IsExpected(ex))
        {
            return path;
        }
    }
}
