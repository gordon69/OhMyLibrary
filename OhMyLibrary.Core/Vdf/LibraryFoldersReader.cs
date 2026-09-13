using System.Collections.ObjectModel;
using System.Globalization;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;
using Serilog;
using Serilog.Core;
using ValveKeyValue;

namespace OhMyLibrary.Core.Vdf;

/// <summary>
/// Reads <c>libraryfolders.vdf</c>: where Steam keeps its games on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The file lives at two paths. <c>&lt;steam&gt;/steamapps/libraryfolders.vdf</c> is canonical and
/// <c>&lt;steam&gt;/config/libraryfolders.vdf</c> is the fallback for older clients.
/// </para>
/// <para>
/// Entries are index-keyed (<c>"0"</c>, <c>"1"</c>, …) rather than an array, and every one of them
/// may point at a drive that is not currently mounted, so each path is normalised and existence
/// checked; unreachable folders are dropped and the rest are still returned. The Steam folder
/// itself is always included when it exists on disk, even if the file is missing or unparseable —
/// otherwise a client that has never had a second library would show an empty launcher.
/// </para>
/// </remarks>
public sealed class LibraryFoldersReader : ILibraryFoldersReader
{
    private const string RootKey = "libraryfolders";
    private const string SteamAppsFolderName = "steamapps";
    private const string ConfigFolderName = "config";
    private const string FileName = "libraryfolders.vdf";
    private const string FileKind = "library folders file";

    private static readonly IReadOnlyDictionary<int, long> NoApps = ReadOnlyDictionary<int, long>.Empty;

    private readonly ILogger _logger;

    /// <summary>Creates a reader.</summary>
    /// <param name="logger">
    /// Logger for skipped entries. <see langword="null"/> is accepted and silences the reader.
    /// </param>
    public LibraryFoldersReader(ILogger? logger = null) => _logger = (logger ?? Logger.None).ForContext<LibraryFoldersReader>();

    /// <inheritdoc />
    public IReadOnlyList<SteamLibraryFolder> Read(string steamPath)
    {
        var root = NormaliseExistingDirectory(steamPath);
        if (root is null)
        {
            _logger.Warning("No Steam folder at {SteamPath}; reporting no library folders", steamPath);
            return [];
        }

        var folders = new List<SteamLibraryFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var manifest = FindManifest(root);
        if (manifest is null)
        {
            _logger.Warning("No {FileName} under {SteamPath}; using the Steam folder alone", FileName, root);
        }
        else
        {
            var document = VdfFile.TryLoadText(manifest, _logger, FileKind);
            if (document is not null)
            {
                if (!string.Equals(document.Name, RootKey, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warning("{Path} has root key {RootKey}, expected {Expected}; parsing it anyway", manifest, document.Name, RootKey);
                }

                AddEntries(document.Root, folders, seen);
            }
        }

        // The main Steam folder is a library whether or not the file admits it.
        if (seen.Add(root))
        {
            folders.Insert(0, new SteamLibraryFolder(root, string.Empty, 0, NoApps));
        }

        return folders;
    }

    private void AddEntries(KVObject libraryFolders, List<SteamLibraryFolder> folders, HashSet<string> seen)
    {
        foreach (var entry in libraryFolders.Children)
        {
            // Index-keyed children only: newer clients also write scalars such as contentstatsid here.
            if (entry.Key is null
                || !int.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                || !entry.Value.IsCollection)
            {
                continue;
            }

            var declared = entry.Value.GetString("path");
            if (string.IsNullOrWhiteSpace(declared))
            {
                _logger.Warning("Skipping library folder {Index}: it declares no path", entry.Key);
                continue;
            }

            var path = NormaliseExistingDirectory(declared);
            if (path is null)
            {
                _logger.Warning("Skipping library folder {Path}: not reachable, the drive may be unplugged", declared);
                continue;
            }

            if (!seen.Add(path))
            {
                continue;
            }

            folders.Add(new SteamLibraryFolder(
                Path: path,
                Label: entry.Value.GetString("label") ?? string.Empty,
                TotalSize: entry.Value.GetLong("totalsize"),
                Apps: ReadApps(entry.Value)));
        }
    }

    /// <summary>
    /// Reads the <c>apps</c> block — Steam's own app id to size-on-disk index. It lags reality, so
    /// it is only ever a hint; the manifests on disk are the truth.
    /// </summary>
    private static IReadOnlyDictionary<int, long> ReadApps(KVObject folder)
    {
        var apps = folder.Child("apps");
        if (apps is null || !apps.IsCollection)
        {
            return NoApps;
        }

        Dictionary<int, long>? sizes = null;
        foreach (var app in apps.Children)
        {
            if (app.Key is null
                || !int.TryParse(app.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId)
                || !long.TryParse(app.Value.AsScalarString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                continue;
            }

            (sizes ??= new Dictionary<int, long>(apps.Count))[appId] = size;
        }

        return sizes ?? NoApps;
    }

    /// <summary>Picks the canonical manifest path, falling back to the legacy one.</summary>
    private static string? FindManifest(string steamPath)
    {
        var canonical = Path.Combine(steamPath, SteamAppsFolderName, FileName);
        if (File.Exists(canonical))
        {
            return canonical;
        }

        var legacy = Path.Combine(steamPath, ConfigFolderName, FileName);
        return File.Exists(legacy) ? legacy : null;
    }

    /// <summary>
    /// Normalises a path — the registry hands out <c>c:/program files (x86)/steam</c> — and returns
    /// <see langword="null"/> when it is malformed or does not exist.
    /// </summary>
    private static string? NormaliseExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (VdfFile.IsExpected(ex))
        {
            return null;
        }
    }
}
