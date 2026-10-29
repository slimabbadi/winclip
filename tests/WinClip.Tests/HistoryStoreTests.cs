using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WinClip.Core;

namespace WinClip.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("winclip-tests-").FullName;

    private string DatabasePath => Path.Combine(_directory, "history.db");

    private HistoryStore CreateStore(int maxUnpinned = 100) => new(DatabasePath, maxUnpinned, new XorProtector());

    private static ClipItem AddText(HistoryStore store, string text, string? source, DateTimeOffset at) =>
        store.Add(ClipContent.PlainText(text), source, at);

    [Fact]
    public void Recopying_text_moves_it_to_the_top_without_duplicating()
    {
        using var store = CreateStore();
        AddText(store, "first", "notepad", T0);
        AddText(store, "second", "notepad", T0.AddSeconds(1));

        var recopied = AddText(store, "first", "code", T0.AddSeconds(2));

        Assert.Equal(["first", "second"], store.GetAll().Select(i => i.Text));
        Assert.Equal(2, recopied.CopyCount);
        Assert.Equal("code", recopied.SourceApp);
        Assert.Equal(T0, recopied.FirstCopiedAt);
    }

    [Fact]
    public void Recopy_without_a_source_keeps_the_original_source()
    {
        using var store = CreateStore();
        AddText(store, "text", "chrome", T0);

        var recopied = AddText(store, "text", source: null, T0.AddSeconds(1));

        Assert.Equal("chrome", recopied.SourceApp);
    }

    [Fact]
    public void Formats_round_trip_and_are_replaced_by_a_recopy()
    {
        using var store = CreateStore();
        var html = Encoding.UTF8.GetBytes("<b>bold</b>");
        var item = store.Add(new ClipContent(ClipKind.Text, "bold", new Dictionary<string, byte[]> { [ClipFormat.Html] = html }), null, T0);

        Assert.True(item.IsRichText);
        Assert.Equal(html, store.GetFormats(item.Id)[ClipFormat.Html]);

        var plain = store.Add(ClipContent.PlainText("bold"), null, T0.AddSeconds(1));

        Assert.Equal(item.Id, plain.Id);
        Assert.False(plain.IsRichText);
        Assert.Empty(store.GetFormats(item.Id));
    }

    [Fact]
    public void Images_with_the_same_size_but_different_pixels_are_separate_items()
    {
        using var store = CreateStore();
        ClipContent Image(byte marker) => new(ClipKind.Image, "Image, 2 × 2",
            new Dictionary<string, byte[]> { [ClipFormat.Png] = [marker] }, Thumbnail: [marker]);

        store.Add(Image(1), null, T0);
        store.Add(Image(2), null, T0.AddSeconds(1));

        var items = store.GetAll();
        Assert.Equal(2, items.Count);
        Assert.Equal([2], items[0].Thumbnail);
    }

    [Fact]
    public void A_single_copy_is_counted_once()
    {
        using var store = CreateStore();
        var text = AddText(store, "once", null, T0);
        var image = store.Add(new ClipContent(ClipKind.Image, "Image, 1 × 1",
            new Dictionary<string, byte[]> { [ClipFormat.Png] = [7, 7, 7] }, Thumbnail: [7]), null, T0.AddSeconds(1));

        Assert.Equal(1, text.CopyCount);
        Assert.Equal(1, image.CopyCount);
        Assert.All(store.GetAll(), item => Assert.Equal(1, item.CopyCount));
    }

    [Fact]
    public void Text_and_formats_are_encrypted_at_rest()
    {
        using (var store = CreateStore())
        {
            store.Add(new ClipContent(ClipKind.Text, "hunter2",
                new Dictionary<string, byte[]> { [ClipFormat.Rtf] = Encoding.UTF8.GetBytes("{\\rtf hunter2}") }), null, T0);
        }

        using var raw = new SqliteConnection($"Data Source={DatabasePath}");
        raw.Open();
        using var command = raw.CreateCommand();
        command.CommandText = "SELECT text FROM items UNION ALL SELECT data FROM formats;";
        using var reader = command.ExecuteReader();
        var secret = Encoding.UTF8.GetBytes("hunter2");
        var rows = 0;
        while (reader.Read())
        {
            rows++;
            Assert.Equal(-1, reader.GetFieldValue<byte[]>(0).AsSpan().IndexOf(secret));
        }
        Assert.Equal(2, rows);
    }

    [Fact]
    public void Touch_moves_an_item_to_the_top()
    {
        using var store = CreateStore();
        var older = AddText(store, "older", null, T0);
        AddText(store, "newer", null, T0.AddSeconds(1));

        store.Touch(older.Id, T0.AddSeconds(2));

        Assert.Equal(["older", "newer"], store.GetAll().Select(i => i.Text));
    }

    [Fact]
    public void Oldest_unpinned_items_are_trimmed_but_pinned_items_survive()
    {
        using var store = CreateStore(maxUnpinned: 2);
        var pinned = AddText(store, "keep me", null, T0);
        store.SetPinned(pinned.Id, true);
        AddText(store, "a", null, T0.AddSeconds(1));
        AddText(store, "b", null, T0.AddSeconds(2));
        AddText(store, "c", null, T0.AddSeconds(3));

        Assert.Equal(["keep me", "c", "b"], store.GetAll().Select(i => i.Text));
    }

    [Fact]
    public void Lowering_the_cap_trims_immediately()
    {
        using var store = CreateStore(maxUnpinned: 10);
        AddText(store, "old", null, T0);
        AddText(store, "new", null, T0.AddSeconds(1));

        store.MaxUnpinned = 1;

        Assert.Equal(["new"], store.GetAll().Select(i => i.Text));
    }

    [Fact]
    public void Clearing_history_keeps_pinned_items()
    {
        using var store = CreateStore();
        store.SetPinned(AddText(store, "pinned", null, T0).Id, true);
        AddText(store, "loose", null, T0.AddSeconds(1));

        store.ClearUnpinned();

        Assert.Equal(["pinned"], store.GetAll().Select(i => i.Text));
    }

    [Fact]
    public void Deleting_an_item_deletes_its_formats()
    {
        using var store = CreateStore();
        var item = store.Add(new ClipContent(ClipKind.Text, "x",
            new Dictionary<string, byte[]> { [ClipFormat.Html] = [1] }), null, T0);

        store.Delete(item.Id);

        Assert.Empty(store.GetFormats(item.Id));
    }

    [Fact]
    public void History_persists_across_reopen()
    {
        using (var store = CreateStore())
            AddText(store, "survives restart", "notepad", T0);

        using var reopened = CreateStore();
        Assert.Equal("survives restart", Assert.Single(reopened.GetAll()).Text);
    }

    [Fact]
    public void Version_1_history_is_migrated_with_pins_and_timestamps()
    {
        using (var legacy = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE items (id INTEGER PRIMARY KEY, hash TEXT NOT NULL UNIQUE, text TEXT NOT NULL, source_app TEXT,
                    first_copied_at INTEGER NOT NULL, last_copied_at INTEGER NOT NULL,
                    copy_count INTEGER NOT NULL DEFAULT 1, pinned INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX ix_items_recency ON items (pinned DESC, last_copied_at DESC);
                INSERT INTO items VALUES (1, 'h1', 'pinned note', 'notepad', {T0.ToUnixTimeMilliseconds()}, {T0.AddSeconds(5).ToUnixTimeMilliseconds()}, 3, 1);
                INSERT INTO items VALUES (2, 'h2', 'recent', NULL, {T0.ToUnixTimeMilliseconds()}, {T0.AddSeconds(9).ToUnixTimeMilliseconds()}, 1, 0);
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = CreateStore();
        var items = store.GetAll();

        Assert.Equal(["pinned note", "recent"], items.Select(i => i.Text));
        Assert.True(items[0].Pinned);
        Assert.Equal(3, items[0].CopyCount);
        Assert.Equal("notepad", items[0].SourceApp);
        Assert.Equal(T0, items[0].FirstCopiedAt);
        Assert.Equal(T0.AddSeconds(5), items[0].LastCopiedAt);
    }

    [Fact]
    public void Migration_leaves_no_plaintext_in_the_database_files()
    {
        using (var legacy = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE items (id INTEGER PRIMARY KEY, hash TEXT NOT NULL UNIQUE, text TEXT NOT NULL, source_app TEXT,
                    first_copied_at INTEGER NOT NULL, last_copied_at INTEGER NOT NULL,
                    copy_count INTEGER NOT NULL DEFAULT 1, pinned INTEGER NOT NULL DEFAULT 0);
                INSERT INTO items VALUES (1, 'h1', 'correct horse battery staple', NULL, 0, 0, 1, 0);
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using (CreateStore()) { }
        SqliteConnection.ClearAllPools();

        var secret = Encoding.UTF8.GetBytes("correct horse battery staple");
        foreach (var file in Directory.GetFiles(_directory, "history.db*"))
            Assert.Equal(-1, File.ReadAllBytes(file).AsSpan().IndexOf(secret));
    }

    [Fact]
    public void Deleted_items_leave_no_plaintext_behind()
    {
        // XorProtector output is predictable, so search for its ciphertext instead of the plaintext.
        var secret = Encoding.UTF8.GetBytes("delete me please");
        var stored = new XorProtector().Protect(secret);
        using (var store = CreateStore())
        {
            var item = AddText(store, "delete me please", null, T0);
            store.Delete(item.Id);
        }
        SqliteConnection.ClearAllPools();

        foreach (var file in Directory.GetFiles(_directory, "history.db*"))
            Assert.Equal(-1, File.ReadAllBytes(file).AsSpan().IndexOf(stored));
    }

    [Fact]
    public void Unpinning_at_the_cap_never_deletes_the_unpinned_item()
    {
        using var store = CreateStore(maxUnpinned: 2);
        var old = AddText(store, "old", null, T0);
        store.SetPinned(old.Id, true);
        AddText(store, "a", null, T0.AddSeconds(1));
        AddText(store, "b", null, T0.AddSeconds(2));

        store.SetPinned(old.Id, false);

        Assert.Equal(["b", "old"], store.GetAll().Select(i => i.Text));
        AssertMatchesReopen(store);
    }

    [Fact]
    public void Unpinning_an_item_that_is_among_the_newest_trims_the_oldest()
    {
        using var store = CreateStore(maxUnpinned: 2);
        AddText(store, "oldest", null, T0);
        var pinned = AddText(store, "pinned", null, T0.AddSeconds(1));
        store.SetPinned(pinned.Id, true);
        AddText(store, "newest", null, T0.AddSeconds(2));

        store.SetPinned(pinned.Id, false);

        Assert.Equal(["newest", "pinned"], store.GetAll().Select(i => i.Text));
    }

    [Fact]
    public void An_item_recorded_with_an_old_timestamp_at_the_cap_is_kept()
    {
        using var store = CreateStore(maxUnpinned: 2);
        AddText(store, "a", null, T0.AddSeconds(5));
        AddText(store, "b", null, T0.AddSeconds(6));

        AddText(store, "late", null, T0);

        Assert.Contains("late", store.GetAll().Select(i => i.Text));
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void Unreadable_rows_are_skipped_and_counted_but_not_deleted()
    {
        var protector = new FlakyProtector();
        using var store = new HistoryStore(DatabasePath, 100, protector);
        AddText(store, "good", null, T0);
        AddText(store, "poison text", null, T0.AddSeconds(1));
        store.Add(new ClipContent(ClipKind.Image, "Image", new Dictionary<string, byte[]>(), Thumbnail: [0xEE, 0xEE]), null, T0.AddSeconds(2));
        protector.FailOnContaining = [(byte)('p' ^ 0x5A), (byte)('o' ^ 0x5A), (byte)('i' ^ 0x5A)];
        using var reader = new HistoryStore(DatabasePath, 100, protector);

        Assert.Equal(["good", "Image"], reader.GetAll().Select(i => i.Text).Order());
        Assert.Equal(1, reader.UnreadableItemCount);

        protector.FailOnContaining = [];
        using var recovered = new HistoryStore(DatabasePath, 100, protector);
        Assert.Equal(3, recovered.GetAll().Count);
        Assert.Equal(0, recovered.UnreadableItemCount);
    }

    [Fact]
    public void Unreadable_thumbnails_are_skipped_too()
    {
        var protector = new FlakyProtector();
        using (var store = new HistoryStore(DatabasePath, 100, protector))
        {
            AddText(store, "good", null, T0);
            store.Add(new ClipContent(ClipKind.Image, "Image", new Dictionary<string, byte[]>(), Thumbnail: [1, 2, 3]), null, T0.AddSeconds(1));
        }
        protector.FailOnContaining = [1 ^ 0x5A, 2 ^ 0x5A];
        using var reader = new HistoryStore(DatabasePath, 100, protector);

        Assert.Equal("good", Assert.Single(reader.GetAll()).Text);
        Assert.Equal(1, reader.UnreadableItemCount);
    }

    [Fact]
    public void Unreadable_count_follows_deletes_and_clears()
    {
        var protector = new FlakyProtector();
        using (var store = new HistoryStore(DatabasePath, 100, protector))
        {
            AddText(store, "poison one", null, T0);
            store.SetPinned(AddText(store, "poison two", null, T0.AddSeconds(1)).Id, true);
            AddText(store, "fine", null, T0.AddSeconds(2));
        }
        protector.FailOnContaining = [(byte)('p' ^ 0x5A), (byte)('o' ^ 0x5A), (byte)('i' ^ 0x5A)];
        using var reader = new HistoryStore(DatabasePath, 100, protector);
        reader.GetAll();
        Assert.Equal(2, reader.UnreadableItemCount);

        reader.ClearUnpinned();

        Assert.Equal(1, reader.UnreadableItemCount);
        Assert.Empty(reader.GetAll());
    }

    [Fact]
    public void Snapshot_stays_consistent_with_a_fresh_reopen_after_every_kind_of_mutation()
    {
        using var store = CreateStore(maxUnpinned: 3);
        Assert.Empty(store.GetAll()); // Load the snapshot first so each step below updates it incrementally.

        var a = AddText(store, "a", "x", T0);
        AssertMatchesReopen(store);
        var b = AddText(store, "b", null, T0.AddSeconds(1));
        AssertMatchesReopen(store);
        AddText(store, "a", "y", T0.AddSeconds(2)); // Recopy.
        AssertMatchesReopen(store);
        store.Touch(b.Id, T0.AddSeconds(3));
        AssertMatchesReopen(store);
        store.SetPinned(a.Id, true);
        AssertMatchesReopen(store);
        AddText(store, "c", null, T0.AddSeconds(4));
        AddText(store, "d", null, T0.AddSeconds(5));
        AddText(store, "e", null, T0.AddSeconds(6)); // Trims "b".
        AssertMatchesReopen(store);
        Assert.DoesNotContain("b", store.GetAll().Select(i => i.Text));
        store.SetPinned(a.Id, false);
        AssertMatchesReopen(store);
        store.MaxUnpinned = 1;
        AssertMatchesReopen(store);
        var f = AddText(store, "f", null, T0.AddSeconds(7));
        store.Delete(f.Id);
        AssertMatchesReopen(store);
        store.SetPinned(AddText(store, "g", null, T0.AddSeconds(8)).Id, true);
        AddText(store, "h", null, T0.AddSeconds(9));
        store.ClearUnpinned();
        AssertMatchesReopen(store);
        Assert.Equal(["g"], store.GetAll().Select(i => i.Text));
    }

    [Fact]
    public void GetAll_returns_a_snapshot_that_later_mutations_do_not_change()
    {
        using var store = CreateStore();
        AddText(store, "one", null, T0);
        var before = store.GetAll();

        AddText(store, "two", null, T0.AddSeconds(1));

        Assert.Equal(["one"], before.Select(i => i.Text));
        Assert.Equal(["two", "one"], store.GetAll().Select(i => i.Text));
        Assert.False(before is IList<ClipItem> { IsReadOnly: false });
    }

    [Fact]
    public void Concurrent_use_keeps_the_store_consistent()
    {
        using var store = CreateStore(maxUnpinned: 20);
        Parallel.For(0, 200, i =>
        {
            var item = AddText(store, $"item {i}", null, T0.AddSeconds(i));
            store.Touch(item.Id, T0.AddSeconds(i + 1000));
            Assert.NotNull(store.GetAll());
            if (i % 7 == 0)
                store.Delete(item.Id);
        });

        AssertMatchesReopen(store);
    }

    [Fact]
    public void Item_text_is_shortened_but_the_full_text_stays_available_and_is_used_for_duplicates()
    {
        using var store = CreateStore();
        var shared = new string('x', ClipItem.MaxTextLength);
        var first = AddText(store, shared + "one", null, T0);
        var second = AddText(store, shared + "two", null, T0.AddSeconds(1));

        Assert.NotEqual(first.Id, second.Id);
        Assert.All(store.GetAll(), item => Assert.Equal(shared, item.Text));
        Assert.Equal(shared + "one", store.GetFullText(first.Id));
        Assert.Equal(shared + "two", store.GetFullText(second.Id));
        Assert.Null(store.GetFullText(9999));
        Assert.Equal(2, AddText(store, shared + "one", null, T0.AddSeconds(2)).CopyCount);
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void Shortening_does_not_split_a_surrogate_pair()
    {
        using var store = CreateStore();
        AddText(store, new string('x', ClipItem.MaxTextLength - 1) + "😀tail", null, T0);

        var text = Assert.Single(store.GetAll()).Text;

        Assert.Equal(ClipItem.MaxTextLength - 1, text.Length);
    }

    private void AssertMatchesReopen(HistoryStore store)
    {
        static object[] Project(IReadOnlyList<ClipItem> items) => items
            .Select(i => (object)(i.Id, i.Kind, i.Text, i.Thumbnail?.Length, i.IsRichText, i.SourceApp,
                i.FirstCopiedAt, i.LastCopiedAt, i.CopyCount, i.Pinned))
            .ToArray();

        using var fresh = CreateStore(store.MaxUnpinned);
        Assert.Equal(Project(fresh.GetAll()), Project(store.GetAll()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Reversible and visibly different from plaintext, which is all these tests need.</summary>
    private sealed class XorProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] ciphertext) => Protect(ciphertext);
    }

    /// <summary>XOR-like protector that refuses to decrypt ciphertext containing a given byte sequence.</summary>
    private sealed class FlakyProtector : IDataProtector
    {
        public byte[] FailOnContaining { get; set; } = [];

        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] ciphertext)
        {
            if (FailOnContaining.Length > 0 && ciphertext.AsSpan().IndexOf(FailOnContaining) >= 0)
                throw new CryptographicException("Simulated DPAPI failure.");
            return Protect(ciphertext);
        }
    }
}
