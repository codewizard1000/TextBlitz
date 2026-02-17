using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using TextBlitz.Models;

namespace TextBlitz.Data;

public sealed class DatabaseService : IDisposable
{
    private static readonly string DbDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TextBlitz");

    private static readonly string DbPath = Path.Combine(DbDirectory, "textblitz.db");

    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public DatabaseService()
    {
        Directory.CreateDirectory(DbDirectory);
        _connectionString = $"Data Source={DbPath}";
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // ─── Initialization ──────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = CreateConnection();

            // Enable WAL mode for better concurrent read performance
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");

            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS SchemaVersion (
                    Version INTEGER NOT NULL
                );
            ");

            var version = await connection.ExecuteScalarAsync<int?>(
                "SELECT MAX(Version) FROM SchemaVersion;");

            if (version is null or 0)
            {
                await ApplyMigrationV1Async(connection);
            }

            // Future migrations go here:
            // if (version < 2) await ApplyMigrationV2Async(connection);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task ApplyMigrationV1Async(SqliteConnection connection)
    {
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Snippets (
                Id              TEXT PRIMARY KEY NOT NULL,
                Name            TEXT NOT NULL DEFAULT '',
                Content         TEXT NOT NULL DEFAULT '',
                TextShortcut    TEXT NOT NULL DEFAULT '',
                Hotkey          TEXT NOT NULL DEFAULT '',
                IsEnabled       INTEGER NOT NULL DEFAULT 1,
                CreatedAt       TEXT NOT NULL,
                UpdatedAt       TEXT NOT NULL,
                SyncId          TEXT
            );

            CREATE TABLE IF NOT EXISTS ClipboardItems (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                PlainText       TEXT NOT NULL DEFAULT '',
                RichText        TEXT,
                HtmlText        TEXT,
                SourceApp       TEXT,
                Timestamp       TEXT NOT NULL,
                IsPinned        INTEGER NOT NULL DEFAULT 0,
                PinOrder        INTEGER NOT NULL DEFAULT 0,
                ListId          INTEGER,
                FOREIGN KEY (ListId) REFERENCES SavedLists(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS SavedLists (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                Name            TEXT NOT NULL DEFAULT '',
                CreatedAt       TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS KeyValueStore (
                Key             TEXT PRIMARY KEY NOT NULL,
                Value           TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ClipboardItems_Timestamp
                ON ClipboardItems(Timestamp DESC);

            CREATE INDEX IF NOT EXISTS IX_ClipboardItems_IsPinned
                ON ClipboardItems(IsPinned) WHERE IsPinned = 1;

            CREATE INDEX IF NOT EXISTS IX_ClipboardItems_ListId
                ON ClipboardItems(ListId) WHERE ListId IS NOT NULL;

            CREATE INDEX IF NOT EXISTS IX_Snippets_TextShortcut
                ON Snippets(TextShortcut) WHERE TextShortcut != '';

            INSERT INTO SchemaVersion (Version) VALUES (1);
        ");
    }

    // ─── Snippets ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Snippet>> GetAllSnippetsAsync()
    {
        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<SnippetRow>(
            "SELECT * FROM Snippets ORDER BY Name;");

        return rows.Select(MapToSnippet).ToList();
    }

    public async Task SaveSnippetAsync(Snippet snippet)
    {
        snippet.UpdatedAt = DateTime.UtcNow;

        using var connection = CreateConnection();
        await connection.ExecuteAsync(@"
            INSERT INTO Snippets (Id, Name, Content, TextShortcut, Hotkey, IsEnabled, CreatedAt, UpdatedAt, SyncId)
            VALUES (@Id, @Name, @Content, @TextShortcut, @Hotkey, @IsEnabled, @CreatedAt, @UpdatedAt, @SyncId)
            ON CONFLICT(Id) DO UPDATE SET
                Name         = excluded.Name,
                Content      = excluded.Content,
                TextShortcut = excluded.TextShortcut,
                Hotkey       = excluded.Hotkey,
                IsEnabled    = excluded.IsEnabled,
                UpdatedAt    = excluded.UpdatedAt,
                SyncId       = excluded.SyncId;
        ", new
        {
            snippet.Id,
            snippet.Name,
            snippet.Content,
            snippet.TextShortcut,
            snippet.Hotkey,
            IsEnabled = snippet.IsEnabled ? 1 : 0,
            CreatedAt = snippet.CreatedAt.ToString("o"),
            UpdatedAt = snippet.UpdatedAt.ToString("o"),
            snippet.SyncId
        });
    }

    public async Task DeleteSnippetAsync(string id)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync("DELETE FROM Snippets WHERE Id = @Id;", new { Id = id });
    }

    // ─── Clipboard Items ─────────────────────────────────────────────

    public async Task<IReadOnlyList<ClipboardItem>> GetHistoryAsync(int limit = 500)
    {
        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<ClipboardItemRow>(
            "SELECT * FROM ClipboardItems ORDER BY Timestamp DESC LIMIT @Limit;",
            new { Limit = limit });

        return rows.Select(MapToClipboardItem).ToList();
    }

    public async Task<int> SaveClipboardItemAsync(ClipboardItem item)
    {
        using var connection = CreateConnection();
        var id = await connection.ExecuteScalarAsync<int>(@"
            INSERT INTO ClipboardItems (PlainText, RichText, HtmlText, SourceApp, Timestamp, IsPinned, PinOrder, ListId)
            VALUES (@PlainText, @RichText, @HtmlText, @SourceApp, @Timestamp, @IsPinned, @PinOrder, @ListId)
            RETURNING Id;
        ", new
        {
            item.PlainText,
            item.RichText,
            item.HtmlText,
            item.SourceApp,
            Timestamp = item.Timestamp.ToString("o"),
            IsPinned = item.IsPinned ? 1 : 0,
            item.PinOrder,
            item.ListId
        });

        item.Id = id;
        return id;
    }

    public async Task DeleteClipboardItemAsync(int id)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync("DELETE FROM ClipboardItems WHERE Id = @Id;", new { Id = id });
    }

    public async Task UpdatePinAsync(int id, bool isPinned, int pinOrder = 0)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE ClipboardItems SET IsPinned = @IsPinned, PinOrder = @PinOrder WHERE Id = @Id;
        ", new { Id = id, IsPinned = isPinned ? 1 : 0, PinOrder = pinOrder });
    }

    public async Task<IReadOnlyList<ClipboardItem>> GetPinnedItemsAsync()
    {
        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<ClipboardItemRow>(
            "SELECT * FROM ClipboardItems WHERE IsPinned = 1 ORDER BY PinOrder, Timestamp DESC;");

        return rows.Select(MapToClipboardItem).ToList();
    }

    // ─── Saved Lists ─────────────────────────────────────────────────

    public async Task<int> CreateListAsync(string name)
    {
        using var connection = CreateConnection();
        return await connection.ExecuteScalarAsync<int>(@"
            INSERT INTO SavedLists (Name, CreatedAt) VALUES (@Name, @CreatedAt) RETURNING Id;
        ", new { Name = name, CreatedAt = DateTime.UtcNow.ToString("o") });
    }

    public async Task<IReadOnlyList<SavedList>> GetListsAsync()
    {
        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<SavedListRow>(
            "SELECT * FROM SavedLists ORDER BY Name;");

        return rows.Select(r => new SavedList
        {
            Id = r.Id,
            Name = r.Name,
            CreatedAt = DateTime.Parse(r.CreatedAt)
        }).ToList();
    }

    public async Task<IReadOnlyList<ClipboardItem>> GetListItemsAsync(int listId)
    {
        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<ClipboardItemRow>(
            "SELECT * FROM ClipboardItems WHERE ListId = @ListId ORDER BY PinOrder, Timestamp DESC;",
            new { ListId = listId });

        return rows.Select(MapToClipboardItem).ToList();
    }

    public async Task AddItemToListAsync(int clipboardItemId, int listId)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE ClipboardItems SET ListId = @ListId WHERE Id = @Id;",
            new { Id = clipboardItemId, ListId = listId });
    }

    public async Task DeleteListAsync(int listId)
    {
        using var connection = CreateConnection();
        // Remove list association from items first, then delete the list
        await connection.ExecuteAsync(
            "UPDATE ClipboardItems SET ListId = NULL WHERE ListId = @ListId;",
            new { ListId = listId });
        await connection.ExecuteAsync(
            "DELETE FROM SavedLists WHERE Id = @Id;",
            new { Id = listId });
    }

    // ─── Settings ────────────────────────────────────────────────────

    private const string SettingsKey = "app_settings";
    private const string UserProfileKey = "user_profile";

    public async Task<AppSettings> GetSettingsAsync()
    {
        var json = await GetKeyValueAsync(SettingsKey);
        if (json is null)
            return new AppSettings();

        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await SetKeyValueAsync(SettingsKey, json);
    }

    // ─── User Profile ────────────────────────────────────────────────

    public async Task<UserProfile?> GetUserProfileAsync()
    {
        var json = await GetKeyValueAsync(UserProfileKey);
        if (json is null)
            return null;

        return JsonSerializer.Deserialize<UserProfile>(json, JsonOptions);
    }

    public async Task SaveUserProfileAsync(UserProfile profile)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await SetKeyValueAsync(UserProfileKey, json);
    }

    // ─── Key-Value Helpers ───────────────────────────────────────────

    private async Task<string?> GetKeyValueAsync(string key)
    {
        using var connection = CreateConnection();
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT Value FROM KeyValueStore WHERE Key = @Key;",
            new { Key = key });
    }

    private async Task SetKeyValueAsync(string key, string value)
    {
        using var connection = CreateConnection();
        await connection.ExecuteAsync(@"
            INSERT INTO KeyValueStore (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
        ", new { Key = key, Value = value });
    }

    // ─── Row Mapping Types ───────────────────────────────────────────

    private sealed class SnippetRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string TextShortcut { get; set; } = string.Empty;
        public string Hotkey { get; set; } = string.Empty;
        public int IsEnabled { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public string? SyncId { get; set; }
    }

    private sealed class ClipboardItemRow
    {
        public int Id { get; set; }
        public string PlainText { get; set; } = string.Empty;
        public string? RichText { get; set; }
        public string? HtmlText { get; set; }
        public string? SourceApp { get; set; }
        public string Timestamp { get; set; } = string.Empty;
        public int IsPinned { get; set; }
        public int PinOrder { get; set; }
        public int? ListId { get; set; }
    }

    private sealed class SavedListRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }

    // ─── Row-to-Model Mappers ────────────────────────────────────────

    private static Snippet MapToSnippet(SnippetRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Content = row.Content,
        TextShortcut = row.TextShortcut,
        Hotkey = row.Hotkey,
        IsEnabled = row.IsEnabled != 0,
        CreatedAt = DateTime.Parse(row.CreatedAt),
        UpdatedAt = DateTime.Parse(row.UpdatedAt),
        SyncId = row.SyncId
    };

    private static ClipboardItem MapToClipboardItem(ClipboardItemRow row) => new()
    {
        Id = row.Id,
        PlainText = row.PlainText,
        RichText = row.RichText,
        HtmlText = row.HtmlText,
        SourceApp = row.SourceApp,
        Timestamp = DateTime.Parse(row.Timestamp),
        IsPinned = row.IsPinned != 0,
        PinOrder = row.PinOrder,
        ListId = row.ListId
    };

    // ─── Dispose ─────────────────────────────────────────────────────

    public void Dispose()
    {
        _lock.Dispose();
    }
}
