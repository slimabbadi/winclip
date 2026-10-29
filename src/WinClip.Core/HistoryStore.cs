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

    // Decoding is strict so a corrupt row is detected and skipped; encoding stays lenient, because clipboard text
    // from a misbehaving app can contain a lone surrogate, which is stored as U+FFFD rather than losing the copy.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;
    private readonly IDataProtector _protector;
    private readonly byte[] _hashKey;
    private int _maxUnpinned;

    // Decrypted view of the table, loaded on first GetAll and kept in step with every mutation (guarded by _gate).
    // Rows that failed to decrypt are not in it; _unreadable maps their id to whether they are pinned.
    private Dictionary<long, ClipItem>? _snapshot;
    private readonly Dictionary<long, bool> _unreadable = [];
    private IReadOnlyList<ClipItem>? _ordered;

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
            lock (_gate)
            {
                _maxUnpinned = value;
                var removed = new List<long>();
                InTransaction(() => removed = TrimUnpinned(keepId: null));
                ApplyRemoved(removed);
            }
        }
    }

    /// <summary>
    /// Number of rows the last <see cref="GetAll"/> skipped because their text or thumbnail could not be
    /// decrypted or decoded. They stay in the database, since decryption failures can be transient.
    /// </summary>
    public int UnreadableItemCount
    {
        get
        {
            lock (_gate)
                return _unreadable.Count;
        }
    }

    /// <summary>Records a copy. A null <paramref name="sourceApp"/> keeps the source recorded earlier.</summary>
    /// <returns>The stored item; its <see cref="ClipItem.Text"/> is shortened like in <see cref="GetAll"/>.</returns>
    public ClipItem Add(ClipContent content, string? sourceApp, DateTimeOffset now)
    {
        lock (_gate)
        {
            ClipItem item = null!;
            var removed = new List<long>();
            InTransaction(() =>
            {
                item = Upsert(_hashKey, content, sourceApp, now);
                // Never trim the item just recorded, even when it is older than everything else.
                removed = TrimUnpinned(item.Pinned ? null : item.Id);
            });
            if (_snapshot is not null)
            {
                _snapshot[item.Id] = item;
                _unreadable.Remove(item.Id);
                _ordered = null;
            }
            ApplyRemoved(removed);
            return item;
        }
    }

    /// <summary>
    /// Pinned items first, then most recently copied. The list is an immutable snapshot, safe to hold on
    /// any thread; an item's <see cref="ClipItem.Text"/> is shortened to <see cref="ClipItem.MaxTextLength"/>.
    /// </summary>
    public IReadOnlyList<ClipItem> GetAll()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _ordered ??= BuildOrdered();
        }
    }

    /// <summary>The complete text of an item, or null when it no longer exists.</summary>
    /// <exception cref="CryptographicException">The stored text cannot be decrypted.</exception>
    public string? GetFullText(long id)
    {
        lock (_gate)
        {
            using var command = Command("SELECT text FROM items WHERE id = $id;", ("$id", id));
            return command.ExecuteScalar() is byte[] stored ? DecodeText(stored) : null;
        }
    }

    /// <summary>The extra formats stored for an item, keyed by <see cref="ClipFormat"/> names.</summary>
    public IReadOnlyDictionary<string, byte[]> GetFormats(long id)
    {
        lock (_gate)
        {
            using var command = Command("SELECT name, data FROM formats WHERE item_id = $id;", ("$id", id));
            using var reader = command.ExecuteReader();
            var formats = new Dictionary<string, byte[]>();
            while (reader.Read())
                formats[reader.GetString(0)] = _protector.Unprotect(reader.GetFieldValue<byte[]>(1));
            return formats;
        }
    }

    /// <summary>Moves an item to the top, as when it is pasted from history.</summary>
    public void Touch(long id, DateTimeOffset now)
    {
        lock (_gate)
        {
            var changed = Execute(
                "UPDATE items SET last_copied_at = $now, copy_count = copy_count + 1 WHERE id = $id;",
                ("$now", now.ToUnixTimeMilliseconds()), ("$id", id));
            if (changed > 0 && _snapshot is not null && _snapshot.TryGetValue(id, out var item))
            {
                _snapshot[id] = item with { LastCopiedAt = now, CopyCount = item.CopyCount + 1 };
                _ordered = null;
            }
        }
    }

    /// <summary>Pins or unpins an item. Unpinning never deletes that item; a later <see cref="Add"/> trims normally.</summary>
    public void SetPinned(long id, bool pinned)
    {
        lock (_gate)
        {
            var removed = new List<long>();
            var changed = 0;
            InTransaction(() =>
            {
                changed = Execute("UPDATE items SET pinned = $pinned WHERE id = $id;", ("$pinned", pinned ? 1 : 0), ("$id", id));
                if (!pinned && changed > 0)
                    removed = TrimUnpinned(keepId: id);
            });
            if (changed > 0)
            {
                if (_snapshot is not null && _snapshot.TryGetValue(id, out var item))
                    _snapshot[id] = item with { Pinned = pinned };
                if (_unreadable.ContainsKey(id))
                    _unreadable[id] = pinned;
                _ordered = null;
            }
            ApplyRemoved(removed);
        }
    }

    public void Delete(long id)
    {
        lock (_gate)
        {
            Execute("DELETE FROM items WHERE id = $id;", ("$id", id));
            ApplyRemoved([id]);
        }
    }

    public void ClearUnpinned()
    {
        lock (_gate)
        {
            Execute("DELETE FROM items WHERE pinned = 0;");
            if (_snapshot is not null)
            {
                foreach (var id in _snapshot.Values.Where(item => !item.Pinned).Select(item => item.Id).ToList())
                    _snapshot.Remove(id);
            }
            foreach (var id in _unreadable.Where(entry => !entry.Value).Select(entry => entry.Key).ToList())
                _unreadable.Remove(id);
            _ordered = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _connection.Dispose();
    }

    private void EnsureLoaded()
    {
        if (_snapshot is not null)
            return;

        var loaded = new Dictionary<long, ClipItem>();
        _unreadable.Clear();
        using var command = Command($"SELECT {Columns} FROM items;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var item = ReadItem(reader,
                    ClipItem.Shorten(DecodeText(reader.GetFieldValue<byte[]>(2))),
                    reader.IsDBNull(3) ? null : _protector.Unprotect(reader.GetFieldValue<byte[]>(3)));
                loaded[item.Id] = item;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _unreadable[reader.GetInt64(0)] = reader.GetInt64(9) != 0;
            }
        }
        _snapshot = loaded;
    }

    private IReadOnlyList<ClipItem> BuildOrdered()
    {
        var items = _snapshot!.Values.ToArray();
        Array.Sort(items, (a, b) =>
        {
            if (a.Pinned != b.Pinned)
                return a.Pinned ? -1 : 1;
            var byTime = b.LastCopiedAt.CompareTo(a.LastCopiedAt);
            return byTime != 0 ? byTime : b.Id.CompareTo(a.Id);
        });
        return Array.AsReadOnly(items);
    }

    private void ApplyRemoved(IReadOnlyCollection<long> ids)
    {
        foreach (var id in ids)
        {
            _snapshot?.Remove(id);
            _unreadable.Remove(id);
        }
        if (ids.Count > 0)
            _ordered = null;
    }

    private string DecodeText(byte[] stored) => StrictUtf8.GetString(_protector.Unprotect(stored));

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
            item = ReadItem(reader, ClipItem.Shorten(content.Text), content.Thumbnail);
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

    /// <summary>
    /// Deletes unpinned items beyond the cap, oldest first, and returns their ids. <paramref name="keepId"/> is
    /// exempt and counts toward the cap, so the item the caller just wrote or unpinned is never the one dropped.
    /// </summary>
    private List<long> TrimUnpinned(long? keepId)
    {
        var removed = new List<long>();
        if (Scalar("SELECT COUNT(*) FROM items WHERE pinned = 0;") <= _maxUnpinned)
            return removed;

        using var command = Command("""
            DELETE FROM items WHERE id IN (
                SELECT id FROM items WHERE pinned = 0 AND id IS NOT $keep
                ORDER BY last_copied_at DESC, id DESC LIMIT -1 OFFSET $offset)
            RETURNING id;
            """,
            ("$keep", keepId is { } id ? id : DBNull.Value), ("$offset", _maxUnpinned - (keepId is null ? 0 : 1)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            removed.Add(reader.GetInt64(0));
        return removed;
    }

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

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var command = Command(sql);
        return (long)command.ExecuteScalar()!;
    }

    private static ClipItem ReadItem(SqliteDataReader reader, string text, byte[]? thumbnail) => new(
        Id: reader.GetInt64(0),
        Kind: (ClipKind)reader.GetInt32(1),
        Text: text,
        Thumbnail: thumbnail,
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
