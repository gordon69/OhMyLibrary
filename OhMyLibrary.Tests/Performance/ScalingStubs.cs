using Microsoft.Extensions.Options;

using OhMyLibrary.Core.Models;
using OhMyLibrary.Core.Options;
using OhMyLibrary.Core.Services;

namespace OhMyLibrary.Tests.Performance;

/// <summary>An <see cref="IOptionsMonitor{TOptions}"/> over one fixed value.</summary>
/// <typeparam name="T">Options type.</typeparam>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    /// <inheritdoc />
    public T CurrentValue { get; } = value;

    /// <inheritdoc />
    public T Get(string? name) => CurrentValue;

    /// <inheritdoc />
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>An <see cref="IInstallStateService"/> that accepts everything and does nothing.</summary>
public sealed class NoopInstallStateService : IInstallStateService
{
    /// <inheritdoc />
    public Task<AppStateFlags> GetStateAsync(int appId, CancellationToken ct = default) =>
        Task.FromResult(AppStateFlags.FullyInstalled);

    /// <inheritdoc />
    public bool RequestInstall(int appId) => true;

    /// <inheritdoc />
    public bool RequestLaunch(int appId) => true;

    /// <inheritdoc />
    public bool RequestUninstall(int appId) => true;

    /// <inheritdoc />
    public bool RequestValidate(int appId) => true;
}

/// <summary>An <see cref="ICollectionService"/> with no collections.</summary>
public sealed class EmptyCollectionService : ICollectionService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<GameCollection>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GameCollection>>([]);

    /// <inheritdoc />
    public Task<GameCollection> CreateAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task RenameAsync(long collectionId, string name, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteAsync(long collectionId, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task AddGameAsync(long collectionId, int appId, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveGameAsync(long collectionId, int appId, CancellationToken ct = default) => Task.CompletedTask;
}
