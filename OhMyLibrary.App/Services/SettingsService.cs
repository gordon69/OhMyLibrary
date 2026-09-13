using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OhMyLibrary.Core.Options;

namespace OhMyLibrary.App.Services;

/// <summary>
/// Stores the user-editable settings in <c>%LOCALAPPDATA%\OhMyLibrary\user-settings.json</c>.
/// </summary>
/// <remarks>
/// The file mirrors the <c>Steam</c> and <c>Ui</c> sections of <c>appsettings.json</c> so it can be
/// layered on as a configuration source. Writes are atomic (temp file plus move) so a crash mid-save
/// cannot leave a truncated settings file behind.
/// </remarks>
/// <param name="steamOptions">Steam configuration, used as the seed when no settings file exists.</param>
/// <param name="uiOptions">UI configuration, used as the seed when no settings file exists.</param>
/// <param name="logger">Log sink. The API key is never written to it.</param>
public sealed class SettingsService(
    IOptionsMonitor<SteamOptions> steamOptions,
    IOptionsMonitor<UiOptions> uiOptions,
    ILogger<SettingsService> logger) : ISettingsService
{
    /// <summary>Lowest accepted <see cref="UserSettings.CardWidth"/>, in device-independent pixels.</summary>
    public const int MinCardWidth = 120;

    /// <summary>Highest accepted <see cref="UserSettings.CardWidth"/>, in device-independent pixels.</summary>
    public const int MaxCardWidth = 420;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private UserSettings _current = new();
    private bool _loaded;

    /// <inheritdoc />
    public event EventHandler<UserSettingsChangedEventArgs>? Changed;

    /// <inheritdoc />
    public UserSettings Current
    {
        get
        {
            if (!_loaded)
            {
                _current = FromConfiguration();
            }

            return _current;
        }
    }

    /// <inheritdoc />
    public string MaskedApiKey => MaskApiKey(Current.ApiKey);

    /// <inheritdoc />
    public bool HasApiKey => !string.IsNullOrWhiteSpace(Current.ApiKey);

    /// <summary>
    /// Replaces all but the last four characters of an API key with bullets. A key shorter than
    /// five characters is masked entirely, and an empty key masks to an empty string.
    /// </summary>
    /// <param name="apiKey">The key to mask, or <see langword="null"/>.</param>
    public static string MaskApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return string.Empty;
        }

        var trimmed = apiKey.Trim();
        return trimmed.Length <= 4
            ? new string('•', trimmed.Length)
            : string.Concat(new string('•', 12), trimmed.AsSpan(trimmed.Length - 4));
    }

    /// <inheritdoc />
    public async Task<UserSettings> LoadAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var loaded = await ReadFileAsync(ct).ConfigureAwait(false) ?? FromConfiguration();
            _current = Normalise(loaded);
            _loaded = true;
        }
        finally
        {
            _ = _fileLock.Release();
        }

        logger.LogInformation(
            "Settings loaded: theme {Theme}, language {Language}, card width {CardWidth}, api key configured {HasApiKey}, steam id configured {HasSteamId}",
            _current.Theme,
            _current.Language,
            _current.CardWidth,
            !string.IsNullOrEmpty(_current.ApiKey),
            !string.IsNullOrEmpty(_current.SteamId64));

        Changed?.Invoke(this, new UserSettingsChangedEventArgs(_current));
        return _current;
    }

    /// <inheritdoc />
    public async Task<UserSettings> SaveAsync(UserSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalised = Normalise(settings);

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFileAsync(normalised, ct).ConfigureAwait(false);
            _current = normalised;
            _loaded = true;
        }
        finally
        {
            _ = _fileLock.Release();
        }

        Changed?.Invoke(this, new UserSettingsChangedEventArgs(_current));
        return _current;
    }

    private UserSettings FromConfiguration()
    {
        var steam = steamOptions.CurrentValue;
        var ui = uiOptions.CurrentValue;

        return Normalise(new UserSettings
        {
            ApiKey = steam.ApiKey,
            SteamId64 = steam.SteamId64,
            Language = steam.Language,
            Theme = ui.Theme,
            CardWidth = ui.CardWidth,
        });
    }

    private async Task<UserSettings?> ReadFileAsync(CancellationToken ct)
    {
        if (!File.Exists(AppPaths.UserSettingsFile))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                AppPaths.UserSettingsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);

            var file = await JsonSerializer
                .DeserializeAsync<SettingsFile>(stream, SerializerOptions, ct)
                .ConfigureAwait(false);

            if (file is null)
            {
                return null;
            }

            var seed = FromConfiguration();
            return new UserSettings
            {
                ApiKey = file.Steam?.ApiKey ?? seed.ApiKey,
                SteamId64 = file.Steam?.SteamId64 ?? seed.SteamId64,
                Language = file.Steam?.Language ?? seed.Language,
                Theme = file.Ui?.Theme ?? seed.Theme,
                CardWidth = file.Ui?.CardWidth ?? seed.CardWidth,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Settings file at {Path} is unreadable; falling back to configuration", AppPaths.UserSettingsFile);
            return null;
        }
    }

    private async Task WriteFileAsync(UserSettings settings, CancellationToken ct)
    {
        var file = new SettingsFile
        {
            Steam = new SteamSection
            {
                ApiKey = settings.ApiKey,
                SteamId64 = settings.SteamId64,
                Language = settings.Language,
            },
            Ui = new UiSection
            {
                Theme = settings.Theme,
                CardWidth = settings.CardWidth,
            },
        };

        var temporaryPath = AppPaths.UserSettingsFile + ".tmp";

        try
        {
            _ = Directory.CreateDirectory(AppPaths.Root);

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, file, SerializerOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temporaryPath, AppPaths.UserSettingsFile, overwrite: true);
            logger.LogInformation("Settings saved to {Path}", AppPaths.UserSettingsFile);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            logger.LogError(ex, "Could not write settings to {Path}; the values stay in memory only", AppPaths.UserSettingsFile);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not remove the temporary settings file {Path}", path);
        }
    }

    private UserSettings Normalise(UserSettings settings)
    {
        var steamId = (settings.SteamId64 ?? string.Empty).Trim();
        if (steamId.Length > 0 && !ulong.TryParse(steamId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            logger.LogWarning("Discarding a SteamID64 that is not a positive decimal number");
            steamId = string.Empty;
        }

        var language = (settings.Language ?? string.Empty).Trim().ToLowerInvariant();
        if (language.Length == 0)
        {
            language = "english";
        }

        return new UserSettings
        {
            ApiKey = (settings.ApiKey ?? string.Empty).Trim(),
            SteamId64 = steamId,
            Language = language,
            Theme = NormaliseTheme(settings.Theme),
            CardWidth = Math.Clamp(settings.CardWidth, MinCardWidth, MaxCardWidth),
        };
    }

    private static string NormaliseTheme(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System",
    };

    private sealed class SettingsFile
    {
        public SteamSection? Steam { get; set; }

        public UiSection? Ui { get; set; }
    }

    private sealed class SteamSection
    {
        public string? ApiKey { get; set; }

        public string? SteamId64 { get; set; }

        public string? Language { get; set; }
    }

    private sealed class UiSection
    {
        public string? Theme { get; set; }

        public int? CardWidth { get; set; }
    }
}
