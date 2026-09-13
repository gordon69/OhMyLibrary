using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using OhMyLibrary.App.Services;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Services;
using OhMyLibrary.Data;

using Wpf.Ui;
using Wpf.Ui.Controls;

namespace OhMyLibrary.App.ViewModels.Pages;

/// <summary>
/// One detected Steam library folder, shown read-only as a diagnostic.
/// </summary>
/// <param name="Path">Absolute path of the library root.</param>
/// <param name="Detail">Label, app count and reported capacity, already formatted.</param>
public sealed record LibraryFolderInfo(string Path, string Detail);

/// <summary>
/// The settings page: the Steam credentials, presentation preferences, read-only diagnostics about
/// the detected Steam installation, and the manual refresh and cache-clearing actions.
/// </summary>
/// <remarks>
/// <para>
/// The page edits a working copy and only <see cref="ISettingsService.SaveAsync"/> makes anything
/// live, so a half-typed API key never becomes configuration. The theme is the one exception: it is
/// saved the moment it changes, because "applied live" is the point of a theme picker, and it is
/// saved on top of <see cref="ISettingsService.Current"/> rather than the working copy so unsaved
/// edits elsewhere on the page do not ride along.
/// </para>
/// <para>
/// The API key is never rendered in full unless the user asks: the field is masked, the reveal is
/// an explicit toggle, and nothing here ever writes the key to a log.
/// </para>
/// </remarks>
public partial class SettingsViewModel : ViewModelBase
{
    /// <summary>Where a Steam Web API key is issued.</summary>
    public const string ApiKeyUrl = "https://steamcommunity.com/dev/apikey";

    private readonly ISettingsService _settings;
    private readonly ISteamPathResolver _steam;
    private readonly IGameLibraryService _library;
    private readonly IFriendsService _friends;
    private readonly IImageCacheService _images;
    private readonly ISnackbarService _snackbar;

    private bool _initialised;
    private bool _applying;

    /// <summary>Creates the page view model.</summary>
    /// <param name="settings">Reader and writer of the user-editable settings.</param>
    /// <param name="steam">Source of the Steam path, library folder and local account diagnostics.</param>
    /// <param name="library">Target of the local, owned and metadata refresh buttons.</param>
    /// <param name="friends">Target of the friends refresh button.</param>
    /// <param name="images">Owner of the image cache the clear action empties.</param>
    /// <param name="connections">Source of the resolved database path.</param>
    /// <param name="snackbar">Confirmation surface for the action buttons.</param>
    /// <param name="logger">Log sink for guarded failures.</param>
    public SettingsViewModel(
        ISettingsService settings,
        ISteamPathResolver steam,
        IGameLibraryService library,
        IFriendsService friends,
        IImageCacheService images,
        IDbConnectionFactory connections,
        ISnackbarService snackbar,
        ILogger<SettingsViewModel> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(steam);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(friends);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(snackbar);

        _settings = settings;
        _steam = steam;
        _library = library;
        _friends = friends;
        _images = images;
        _snackbar = snackbar;

        DatabasePath = connections.DatabasePath;
        Apply(settings.Current);
    }

    /// <summary>The three theme choices, in the order the picker shows them.</summary>
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];

    /// <summary>Store languages offered by the picker. The configured value is added when missing.</summary>
    public ObservableCollection<string> Languages { get; } =
    [
        "english", "brazilian", "bulgarian", "czech", "danish", "dutch", "finnish", "french",
        "german", "greek", "hungarian", "italian", "japanese", "koreana", "latam", "norwegian",
        "polish", "portuguese", "romanian", "russian", "schinese", "spanish", "swedish",
        "tchinese", "thai", "turkish", "ukrainian", "vietnamese",
    ];

    /// <summary>Library folders found on this machine, for the diagnostics panel.</summary>
    public ObservableCollection<LibraryFolderInfo> LibraryFolders { get; } = [];

    /// <summary>The working copy of the Steam Web API key. Masked in the UI unless revealed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApiKey))]
    private string _apiKey = string.Empty;

    /// <summary>The stored key with all but its last four characters replaced.</summary>
    [ObservableProperty]
    private string _maskedApiKey = string.Empty;

    /// <summary>The working copy of the SteamID64, as a decimal string.</summary>
    [ObservableProperty]
    private string _steamId64 = string.Empty;

    /// <summary>The working copy of the store language.</summary>
    [ObservableProperty]
    private string _language = "english";

    /// <summary>The working copy of the theme. Saved as soon as it changes.</summary>
    [ObservableProperty]
    private string _theme = "System";

    /// <summary>
    /// The working copy of the library card width. A double so the slider binds without a converter;
    /// it is rounded on the way to storage.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardWidthText))]
    private double _cardWidth = 200;

    /// <summary>The detected Steam install root, or <see langword="null"/> when Steam is absent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamPathText))]
    [NotifyPropertyChangedFor(nameof(SteamFound))]
    private string? _steamPath;

    /// <summary>The detected <c>steam.exe</c>, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SteamExecutableText))]
    private string? _steamExecutable;

    /// <summary>Which local accounts were found in <c>loginusers.vdf</c>.</summary>
    [ObservableProperty]
    private string _localAccountsText = "Not scanned yet.";

    /// <summary>Result of the last action button, shown next to the buttons.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Progress reported by the friends refresh, which is the slow one.</summary>
    [ObservableProperty]
    private string? _progressMessage;

    /// <summary>Absolute path of the SQLite database backing the library.</summary>
    public string DatabasePath { get; }

    /// <summary>Where the rolling log files are written.</summary>
    public string LogsDirectory => AppPaths.LogsDirectory;

    /// <summary>Root of everything the app writes.</summary>
    public string DataDirectory => AppPaths.Root;

    /// <summary>The running assembly's informational version.</summary>
    public string AppVersion { get; } = ResolveVersion();

    /// <summary>True when the working copy holds a non-empty key.</summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>The card width as a caption, for example <c>"200 px"</c>.</summary>
    public string CardWidthText => $"{(int)Math.Round(CardWidth)} px";

    /// <summary>True when a Steam installation was located.</summary>
    public bool SteamFound => !string.IsNullOrWhiteSpace(SteamPath);

    /// <summary>The Steam path, or an explanation of its absence.</summary>
    public string SteamPathText => SteamPath ?? "Steam was not found on this machine.";

    /// <summary>The Steam executable path, or an explanation of its absence.</summary>
    public string SteamExecutableText => SteamExecutable ?? "steam.exe was not found.";

    /// <summary>Loads settings and diagnostics once, the first time the page is shown.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task InitialiseAsync(CancellationToken ct = default)
    {
        if (_initialised)
        {
            return Task.CompletedTask;
        }

        _initialised = true;

        return RunGuardedAsync(
            async token =>
            {
                Apply(await _settings.LoadAsync(token).ConfigureAwait(true));
                await LoadDiagnosticsAsync(token).ConfigureAwait(true);
            },
            "Loading settings",
            ct);
    }

    [RelayCommand]
    private Task SaveAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                var pending = new UserSettings
                {
                    ApiKey = ApiKey,
                    SteamId64 = SteamId64.Trim(),
                    Language = Language,
                    Theme = Theme,
                    CardWidth = (int)Math.Round(CardWidth),
                };

                // SaveAsync normalises what it stores, so the page re-binds to the result rather
                // than to what was typed.
                Apply(await _settings.SaveAsync(pending, token).ConfigureAwait(true));

                StatusMessage = "Settings saved.";
                Notify("Settings saved", "The new values are in force.", ControlAppearance.Success);
            },
            "Saving settings",
            ct);

    [RelayCommand]
    private void Revert()
    {
        Apply(_settings.Current);
        StatusMessage = "Reverted to the saved values.";
    }

    [RelayCommand]
    private void PasteApiKey()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                StatusMessage = "The clipboard does not hold any text.";
                return;
            }

            ApiKey = Clipboard.GetText().Trim();
            StatusMessage = "Key pasted. Save to apply it.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            // Another process can hold the clipboard open; that is not worth an error banner.
            Logger.LogWarning(ex, "Could not read the clipboard");
            StatusMessage = "The clipboard could not be read.";
        }
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        ApiKey = string.Empty;
        StatusMessage = "Key cleared. Save to apply it.";
    }

    [RelayCommand]
    private Task DetectSteamIdAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                var users = await Task.Run(_steam.GetLocalUsers, token).ConfigureAwait(true);

                // GetLocalUsers already orders most-recent first, then newest timestamp. That
                // fallback matters: loginusers.vdf often has no MostRecent key at all.
                var user = users.FirstOrDefault();
                if (user is null)
                {
                    StatusMessage = "No signed-in account was found in loginusers.vdf.";
                    Notify(
                        "No local account",
                        "Sign in to the Steam client once, then try again.",
                        ControlAppearance.Caution);
                    return;
                }

                SteamId64 = user.SteamId64.ToString(CultureInfo.InvariantCulture);
                StatusMessage = $"Detected {DescribeUser(user.PersonaName, user.AccountName)}. Save to apply it.";
            },
            "Detecting the signed-in account",
            ct);

    [RelayCommand]
    private Task RefreshDiagnosticsAsync(CancellationToken ct) =>
        RunGuardedAsync(LoadDiagnosticsAsync, "Scanning the Steam installation", ct);

    [RelayCommand]
    private Task RescanLocalAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                // Forced: the user pressed "Rescan local files" and expects it to actually rescan,
                // cooldown or not.
                await _library.RefreshLocalAsync(force: true, token).ConfigureAwait(true);
                await _library.RefreshMetadataAsync(force: false, token).ConfigureAwait(true);
                await LoadDiagnosticsAsync(token).ConfigureAwait(true);

                StatusMessage = "Local manifests rescanned.";
                Notify("Rescan finished", "Installed games are up to date.", ControlAppearance.Success);
            },
            "Rescanning local files",
            ct);

    [RelayCommand]
    private Task RefreshOwnedGamesAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                await _library.RefreshRemoteAsync(force: true, token).ConfigureAwait(true);

                var status = await _library.GetStatusAsync(token).ConfigureAwait(true);
                if (status.OwnedListAvailable)
                {
                    StatusMessage = "Owned games refreshed.";
                    Notify("Owned games refreshed", "The library list is up to date.", ControlAppearance.Success);
                    return;
                }

                StatusMessage = status.ApiKeyConfigured
                    ? "The owned-games list could not be read. The profile may be private."
                    : "No API key is configured, so owned games cannot be read.";

                Notify("Owned games unavailable", StatusMessage, ControlAppearance.Caution);
            },
            "Refreshing owned games",
            ct);

    [RelayCommand(IncludeCancelCommand = true)]
    private Task RefreshFriendsAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                var progress = new Progress<string>(message => ProgressMessage = message);

                try
                {
                    await _friends.RefreshAsync(force: true, progress, token).ConfigureAwait(true);
                    StatusMessage = "Friends refreshed.";
                }
                finally
                {
                    ProgressMessage = null;
                }
            },
            "Refreshing friends",
            ct);

    [RelayCommand]
    private Task ClearImageCacheAsync(CancellationToken ct) =>
        RunGuardedAsync(
            async token =>
            {
                _images.ClearMemory();
                var reclaimed = await _images.ClearDiskAsync(token).ConfigureAwait(true);

                StatusMessage = $"Image cache cleared, {FormatBytes(reclaimed)} reclaimed.";
                Notify("Cache cleared", StatusMessage, ControlAppearance.Success);
            },
            "Clearing the image cache",
            ct);

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(AppPaths.LogsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppPaths.Root);

    [RelayCommand]
    private void OpenSteamFolder()
    {
        if (SteamPath is { Length: > 0 } path)
        {
            OpenFolder(path);
        }
    }

    private async Task LoadDiagnosticsAsync(CancellationToken ct)
    {
        // Registry reads and directory probes: cheap, but not the dispatcher's job.
        var snapshot = await Task.Run(
            () =>
            {
                var path = _steam.FindSteamPath();
                var executable = _steam.FindSteamExecutable();
                var folders = _steam.GetLibraryFolders()
                    .Select(folder => new LibraryFolderInfo(
                        folder.Path,
                        DescribeFolder(folder.Label, folder.Apps.Count, folder.TotalSize)))
                    .ToArray();
                var users = _steam.GetLocalUsers();

                return (path, executable, folders, users);
            },
            ct).ConfigureAwait(true);

        SteamPath = snapshot.path;
        SteamExecutable = snapshot.executable;

        LibraryFolders.Clear();
        foreach (var folder in snapshot.folders)
        {
            LibraryFolders.Add(folder);
        }

        LocalAccountsText = snapshot.users.Count == 0
            ? "No account has signed in on this machine."
            : string.Join(", ", snapshot.users.Select(user => DescribeUser(user.PersonaName, user.AccountName)));
    }

    private void Apply(UserSettings settings)
    {
        _applying = true;

        try
        {
            ApiKey = settings.ApiKey;
            SteamId64 = settings.SteamId64;
            Theme = Themes.FirstOrDefault(
                candidate => string.Equals(candidate, settings.Theme, StringComparison.OrdinalIgnoreCase))
                ?? "System";
            CardWidth = settings.CardWidth;

            var language = string.IsNullOrWhiteSpace(settings.Language) ? "english" : settings.Language;
            if (!Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                Languages.Add(language);
            }

            Language = Languages.First(
                candidate => string.Equals(candidate, language, StringComparison.OrdinalIgnoreCase));

            MaskedApiKey = _settings.MaskedApiKey;
        }
        finally
        {
            _applying = false;
        }
    }

    private Task ApplyThemeAsync(string theme) =>
        RunGuardedAsync(
            async token =>
            {
                var pending = _settings.Current.Clone();
                pending.Theme = theme;

                var saved = await _settings.SaveAsync(pending, token).ConfigureAwait(true);

                _applying = true;
                try
                {
                    Theme = saved.Theme;
                }
                finally
                {
                    _applying = false;
                }
            },
            "Applying theme");

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                StatusMessage = $"{path} does not exist yet.";
                return;
            }

            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusMessage = $"Opened {path}.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Could not open {Path} in the shell", path);
            StatusMessage = $"Windows refused to open {path}.";
        }
    }

    private void Notify(string title, string? message, ControlAppearance appearance) =>
        _snackbar.Show(
            title,
            message ?? string.Empty,
            appearance,
            new SymbolIcon(SymbolRegular.Info24),
            TimeSpan.FromSeconds(4));

    private static string DescribeUser(string personaName, string accountName) =>
        string.IsNullOrWhiteSpace(personaName) ? accountName : $"{personaName} ({accountName})";

    private static string DescribeFolder(string label, int appCount, long totalSize)
    {
        var parts = new List<string>(3)
        {
            appCount == 1 ? "1 app" : $"{appCount.ToString("N0", CultureInfo.CurrentCulture)} apps",
        };

        if (totalSize > 0)
        {
            parts.Add($"{FormatBytes(totalSize)} volume");
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            parts.Add($"labelled \"{label}\"");
        }

        return string.Join(", ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 bytes";
        }

        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes"
            : $"{value.ToString("N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private static string ResolveVersion()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }

    partial void OnThemeChanged(string value)
    {
        if (_applying || string.IsNullOrEmpty(value)
            || string.Equals(value, _settings.Current.Theme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = ApplyThemeAsync(value);
    }
}
