using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WinClip.Core;

/// <summary>
/// SQLite-backed clipboard history. Identical content is stored once and moved to the top when copied again.
/// Pinned items are exempt from the size cap and from <see cref="ClearUnpinned"/>.
/// </summary>
/// <remarks>
/// Text, thumbnails and formats are encrypted with the supplied <see cref="IDataProtector"/>. Duplicate
/// detection uses a keyed hash (HMAC) so the database does not reveal which items equal a guessed value.
/// </remarks>
public sealed class HistoryStore : IDisposable
{
    private const int SchemaVersion = 2;
    private const string Columns =
        "id, kind, text, thumbnail, rich, source_app, first_copied_at, last_copied_at, copy_count, pinned";

    private readonly SqliteConnection _connection;
    private readonly IDataProtector _protector;
    private readonly byte[] _hashKey;
    private int _maxUnpinned;

    public HistoryStore(string databasePath, int maxUnpinned, IDataProtector protector)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxUnpinned, 1);
        _maxUnpinned = maxUnpinned;
        _protector = protector;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        _connection.Open();
        // secure_delete zeroes deleted content, so a deleted or trimmed item can't be recovered from the file.
        Execute("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA secure_delete = ON;");

        byte[] hashKey = [];
        var migratedPlaintext = false;
        InTransaction(() =>
        {
            Execute("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value BLOB NOT NULL);");
            hashKey = LoadOrCreateHashKey();
            if (Scalar("PRAGMA user_version;") < SchemaVersion)
                migratedPlaintext = MigrateToV2(hashKey);
        });
        _hashKey = hashKey;

        // Rows dropped by the migration were written before secure_delete existed; rebuild the file so
        // their plaintext doesn't survive in free pages or the write-ahead log.
        if (migratedPlaintext)
            Execute("VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>History size cap for unpinned items. Lowering it deletes the oldest items immediately.</summary>
    public int MaxUnpinned
    {
        get => _maxUnpinned;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxUnpinned = value;
            TrimUnpinned();
        }
    }

    /// <summary>Records a copy. A null <paramref name="sourceApp"/> keeps the source recorded earlier.</summary>
    public ClipItem Add(ClipContent content, string? sourceApp, DateTimeOffset now)
    {
        ClipItem item = null!;
        InTransaction(() =>
        {
            item = Upsert(_hashKey, content, sourceApp, now);
            TrimUnpinned();
        });
        return item;
    }

    /// <summary>Pinned items first, then most recently copied.</summary>
    public IReadOnlyList<ClipItem> GetAll()
    {
        using var command = Command($"SELECT {Columns} FROM items ORDER BY pinned DESC, last_copied_at DESC, id DESC;");
        using var reader = command.ExecuteReader();
        var items = new List<ClipItem>();
        while (reader.Read())
            items.Add(ReadItem(reader));
        return items;
    }

    /// <summary>The extra formats stored for an item, keyed by <see cref="ClipFormat"/> names.</summary>
    public IReadOnlyDictionary<string, byte[]> GetFormats(long id)
    {
        using var command = Command("SELECT name, data FROM formats WHERE item_id = $id;", ("$id", id));
        using var reader = command.ExecuteReader();
        var formats = new Dictionary<string, byte[]>();
        while (reader.Read())
            formats[reader.GetString(0)] = _protector.Unprotect(reader.GetFieldValue<byte[]>(1));
        return formats;
    }

    /// <summary>Moves an item to the top, as when it is pasted from history.</summary>
    public void Touch(long id, DateTimeOffset now) => Execute(
        "UPDATE items SET last_copied_at = $now, copy_count = copy_count + 1 WHERE id = $id;",
        ("$now", now.ToUnixTimeMilliseconds()), ("$id", id));

    public void SetPinned(long id, bool pinned)
    {
        Execute("UPDATE items SET pinned = $pinned WHERE id = $id;", ("$pinned", pinned ? 1 : 0), ("$id", id));
        if (!pinned)
            TrimUnpinned();
    }

    public void Delete(long id) => Execute("DELETE FROM items WHERE id = $id;", ("$id", id));

    public void ClearUnpinned() => Execute("DELETE FROM items WHERE pinned = 0;");

    public void Dispose() => _connection.Dispose();

    private ClipItem Upsert(byte[] hashKey, ClipContent content, string? sourceApp, DateTimeOffset now)
    {
        ClipItem item;
        using (var command = Command($"""
            INSERT INTO items (hash, kind, text, thumbnail, rich, source_app, first_copied_at, last_copied_at)
            VALUES ($hash, $kind, $text, $thumbnail, $rich, $source, $now, $now)
            ON CONFLICT (hash) DO UPDATE SET
                text           = excluded.text,
                thumbnail      = excluded.thumbnail,
                rich           = excluded.rich,
                last_copied_at = excluded.last_copied_at,
                copy_count     = copy_count + 1,
                source_app     = COALESCE(excluded.source_app, source_app)
            RETURNING {Columns};
            """,
            ("$hash", Hash(hashKey, content)),
            ("$kind", (int)content.Kind),
            ("$text", _protector.Protect(Encoding.UTF8.GetBytes(content.Text))),
            ("$thumbnail", content.Thumbnail is null ? DBNull.Value : _protector.Protect(content.Thumbnail)),
            ("$rich", IsRich(content.Formats) ? 1 : 0),
            ("$source", (object?)sourceApp ?? DBNull.Value),
            ("$now", now.ToUnixTimeMilliseconds())))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            item = ReadItem(reader);
        }

        // A re-copy may carry different formats (say, the same text without formatting); keep the latest.
        Execute("DELETE FROM formats WHERE item_id = $id;", ("$id", item.Id));
        foreach (var (name, data) in content.Formats)
        {
            Execute("INSERT INTO formats (item_id, name, data) VALUES ($id, $name, $data);",
                ("$id", item.Id), ("$name", name), ("$data", _protector.Protect(data)));
        }
        return item;
    }

    private void TrimUnpinned() => Execute("""
        DELETE FROM items WHERE pinned = 0 AND id NOT IN (
            SELECT id FROM items WHERE pinned = 0 ORDER BY last_copied_at DESC, id DESC LIMIT $max);
        """, ("$max", _maxUnpinned));

    private byte[] LoadOrCreateHashKey()
    {
        using (var command = Command("SELECT value FROM meta WHERE key = 'hash_key';"))
        {
            if (command.ExecuteScalar() is byte[] stored)
                return _protector.Unprotect(stored);
        }
        var key = RandomNumberGenerator.GetBytes(32);
        Execute("INSERT INTO meta (key, value) VALUES ('hash_key', $value);", ("$value", _protector.Protect(key)));
        return key;
    }

    /// <summary>
    /// Version 1 stored plain text only, unencrypted. Its rows are re-added through the normal path so they get
    /// encrypted and hashed, keeping their timestamps, copy counts and pins.
    /// </summary>
    /// <returns>True when version 1 rows were migrated.</returns>
    private bool MigrateToV2(byte[] hashKey)
    {
        var hasLegacyTable = Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'items';") > 0;
        if (hasLegacyTable)
        {
            Execute("DROP INDEX IF EXISTS ix_items_recency;");
            Execute("ALTER TABLE items RENAME TO items_v1;");
        }

        Execute($"""
            CREATE TABLE items (
                id              INTEGER PRIMARY KEY,
                hash            TEXT    NOT NULL UNIQUE,
                kind            INTEGER NOT NULL,
                text            BLOB    NOT NULL,
                thumbnail       BLOB,
                rich            INTEGER NOT NULL DEFAULT 0,
                source_app      TEXT,
                first_copied_at INTEGER NOT NULL,
                last_copied_at  INTEGER NOT NULL,
                copy_count      INTEGER NOT NULL DEFAULT 1,
                pinned          INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX ix_items_recency ON items (pinned DESC, last_copied_at DESC);
            CREATE TABLE formats (
                item_id INTEGER NOT NULL REFERENCES items (id) ON DELETE CASCADE,
                name    TEXT    NOT NULL,
                data    BLOB    NOT NULL,
                PRIMARY KEY (item_id, name)
            );
            PRAGMA user_version = {SchemaVersion};
            """);

        if (!hasLegacyTable)
            return false;

        var legacy = new List<(string Text, string? Source, long First, long Last, int Count, bool Pinned)>();
        using (var command = Command("SELECT text, source_app, first_copied_at, last_copied_at, copy_count, pinned FROM items_v1;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                legacy.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetInt64(5) != 0));
            }
        }

        foreach (var row in legacy)
        {
            var item = Upsert(hashKey, ClipContent.PlainText(row.Text), row.Source,
                DateTimeOffset.FromUnixTimeMilliseconds(row.Last));
            Execute("""
                UPDATE items SET first_copied_at = $first, copy_count = $count, pinned = $pinned WHERE id = $id;
                """,
                ("$first", row.First), ("$count", row.Count), ("$pinned", row.Pinned ? 1 : 0), ("$id", item.Id));
        }
        Execute("DROP TABLE items_v1;");
        return true;
    }

    private void InTransaction(Action action)
    {
        Execute("BEGIN IMMEDIATE;");
        try
        {
            action();
            Execute("COMMIT;");
        }
        catch
        {
            Execute("ROLLBACK;");
            throw;
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var command = Command(sql);
        return (long)command.ExecuteScalar()!;
    }

    private ClipItem ReadItem(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        Kind: (ClipKind)reader.GetInt32(1),
        Text: Encoding.UTF8.GetString(_protector.Unprotect(reader.GetFieldValue<byte[]>(2))),
        Thumbnail: reader.IsDBNull(3) ? null : _protector.Unprotect(reader.GetFieldValue<byte[]>(3)),
        IsRichText: reader.GetInt64(4) != 0,
        SourceApp: reader.IsDBNull(5) ? null : reader.GetString(5),
        FirstCopiedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
        LastCopiedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
        CopyCount: reader.GetInt32(8),
        Pinned: reader.GetInt64(9) != 0);

    private static bool IsRich(IReadOnlyDictionary<string, byte[]> formats) =>
        formats.ContainsKey(ClipFormat.Html) || formats.ContainsKey(ClipFormat.Rtf);

    /// <summary>
    /// Identity for duplicate detection. Formatting is excluded, so recopying the same text with different
    /// styling moves the existing item up instead of adding a near-duplicate.
    /// </summary>
    private static string Hash(byte[] key, ClipContent content)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(Encoding.UTF8.GetBytes($"{(int)content.Kind}\n{content.Text}"));
        if (content.Kind == ClipKind.Image && content.Formats.TryGetValue(ClipFormat.Png, out var png))
            hmac.AppendData(png);
        return Convert.ToHexString(hmac.GetHashAndReset());
    }
}
