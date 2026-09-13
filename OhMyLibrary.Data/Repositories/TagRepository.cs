using Dapper;
using OhMyLibrary.Core.Abstractions;
using OhMyLibrary.Core.Models;

namespace OhMyLibrary.Data.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ITagRepository"/>.
/// </summary>
/// <remarks>
/// Both name tables are keyed by id <i>and</i> language, so several languages can sit side by side
/// and switching language never has to re-download what is already cached. Writes are additive:
/// nothing is ever deleted, so a short or failed download degrades to a stale name rather than none.
/// </remarks>
/// <param name="connectionFactory">Source of database connections.</param>
public sealed class TagRepository(IDbConnectionFactory connectionFactory) : ITagRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TagRef>> GetTagsAsync(string lang, CancellationToken ct = default)
    {
        var rows = await QueryNamesAsync("Tags", "tag_id", lang, ct).ConfigureAwait(false);
        return rows.Select(row => new TagRef(row.Id, row.Name)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GenreRef>> GetGenresAsync(string lang, CancellationToken ct = default)
    {
        var rows = await QueryNamesAsync("Genres", "genre_id", lang, ct).ConfigureAwait(false);
        return rows.Select(row => new GenreRef(row.Id, row.Name)).ToList();
    }

    /// <inheritdoc />
    public Task UpsertTagNamesAsync(IReadOnlyList<TagRef> tags, string lang, CancellationToken ct = default) =>
        UpsertNamesAsync(
            "Tags",
            "tag_id",
            tags.Select(tag => new NameParam { Id = tag.TagId, Lang = lang, Name = tag.Name }).ToList(),
            ct);

    /// <inheritdoc />
    public Task UpsertGenreNamesAsync(IReadOnlyList<GenreRef> genres, string lang, CancellationToken ct = default) =>
        UpsertNamesAsync(
            "Genres",
            "genre_id",
            genres.Select(genre => new NameParam { Id = genre.GenreId, Lang = lang, Name = genre.Name }).ToList(),
            ct);

    private async Task<List<NameRow>> QueryNamesAsync(string table, string idColumn, string lang, CancellationToken ct)
    {
        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);

        var rows = await connection.QueryAsync<NameRow>(new CommandDefinition(
            $"SELECT {idColumn} AS Id, name AS Name FROM {table} WHERE lang = @lang ORDER BY name COLLATE NOCASE",
            new { lang },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <summary>
    /// Writes a whole name table in one transaction over one prepared statement — the populartags
    /// feed is a few hundred rows and should cost a single commit.
    /// </summary>
    private async Task UpsertNamesAsync(string table, string idColumn, List<NameParam> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var connection = await connectionFactory.CreateOpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             INSERT INTO {table} ({idColumn}, lang, name)
             VALUES (@Id, @Lang, @Name)
             ON CONFLICT({idColumn}, lang) DO UPDATE SET name = excluded.name
             """,
            rows,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private sealed class NameRow
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class NameParam
    {
        public int Id { get; set; }

        public string Lang { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }
}
