using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TextBlitz.Models;

namespace TextBlitz.Services.Firebase;

/// <summary>
/// Provides Firebase project configuration values.
/// </summary>
public static class FirebaseConfig
{
    public static string ProjectId { get; set; } = "texblitz";
    public static string ApiKey { get; set; } = "AIzaSyCJuhy0TZTNn_am8N2LduOViIBtkPqyMtM";

    public static string FirestoreBaseUrl =>
        $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents";
}

/// <summary>
/// Syncs snippets and settings to/from Firestore using the REST API.
/// No Firebase SDK required -- uses plain HttpClient with Bearer token auth.
/// </summary>
public sealed class FirestoreSyncService : IDisposable
{
    private readonly HttpClient _http;
    private readonly FirebaseAuthService _authService;
    private readonly ILogger<FirestoreSyncService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FirestoreSyncService(
        HttpClient httpClient,
        FirebaseAuthService authService,
        ILogger<FirestoreSyncService> logger)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ------------------------------------------------------------------ Snippets

    /// <summary>
    /// Pushes local snippets to the Firestore collection users/{userId}/snippets.
    /// Each snippet is stored as its own document keyed by Snippet.Id.
    /// </summary>
    public async Task SyncSnippetsAsync(List<Snippet> localSnippets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localSnippets);
        EnsureAuthenticated();

        foreach (var snippet in localSnippets)
        {
            var path = SnippetDocPath(snippet.Id);
            var body = SnippetToFirestoreFields(snippet);

            using var request = CreatePatchRequest(path, body);
            await ExecuteWithRetryAsync(request, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Synced {Count} snippets to Firestore", localSnippets.Count);
    }

    /// <summary>
    /// Pulls all snippets from the user's Firestore snippets collection.
    /// </summary>
    public async Task<List<Snippet>> PullSnippetsAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();

        var url = $"{FirebaseConfig.FirestoreBaseUrl}/users/{_authService.CurrentUser!.UserId}/snippets";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AttachAuth(request);

        var response = await ExecuteWithRetryAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var result = new List<Snippet>();
        var root = JsonNode.Parse(json);
        var documents = root?["documents"]?.AsArray();
        if (documents is null)
            return result;

        foreach (var doc in documents)
        {
            if (doc is null) continue;
            var snippet = FirestoreFieldsToSnippet(doc);
            if (snippet is not null)
                result.Add(snippet);
        }

        _logger.LogInformation("Pulled {Count} snippets from Firestore", result.Count);
        return result;
    }

    // ------------------------------------------------------------------ Settings

    /// <summary>Pushes the application settings document to Firestore.</summary>
    public async Task SyncSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureAuthenticated();

        var path = SettingsDocPath();
        var body = SettingsToFirestoreFields(settings);

        using var request = CreatePatchRequest(path, body);
        await ExecuteWithRetryAsync(request, ct).ConfigureAwait(false);

        _logger.LogInformation("Settings synced to Firestore");
    }

    /// <summary>Pulls the application settings document from Firestore.</summary>
    public async Task<AppSettings?> PullSettingsAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();

        var url = SettingsDocPath();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AttachAuth(request);

        try
        {
            var response = await ExecuteWithRetryAsync(request, ct).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = JsonNode.Parse(json);
            return doc is null ? null : FirestoreFieldsToSettings(doc);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("No settings document found in Firestore");
            return null;
        }
    }

    // ------------------------------------------------------------------ User document

    /// <summary>
    /// Creates the user document with initial trial dates (60-day trial).
    /// </summary>
    public async Task CreateUserDocAsync(string userId, string email, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        EnsureAuthenticated();

        var now = DateTime.UtcNow;
        var trialEnd = now.AddDays(60);

        var fields = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["email"] = StringValue(email),
                ["trialStart"] = TimestampValue(now),
                ["trialEnd"] = TimestampValue(trialEnd),
                ["subscriptionStatus"] = StringValue(SubscriptionStatus.Trial.ToString()),
                ["planType"] = StringValue(PlanType.Free.ToString()),
                ["createdAt"] = TimestampValue(now)
            }
        };

        var url = $"{FirebaseConfig.FirestoreBaseUrl}/users/{userId}";
        using var request = CreatePatchRequest(url, fields.ToJsonString());
        await ExecuteWithRetryAsync(request, ct).ConfigureAwait(false);

        _logger.LogInformation("Created user document for {UserId} with trial ending {TrialEnd}",
            userId, trialEnd);
    }

    /// <summary>
    /// Gets the user profile document from Firestore, including subscription status.
    /// </summary>
    public async Task<UserProfile?> GetUserProfileAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();

        var url = $"{FirebaseConfig.FirestoreBaseUrl}/users/{_authService.CurrentUser!.UserId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AttachAuth(request);

        try
        {
            var response = await ExecuteWithRetryAsync(request, ct).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = JsonNode.Parse(json);
            if (doc is null) return null;

            return ParseUserProfile(doc);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("User document not found in Firestore");
            return null;
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    // ------------------------------------------------------------------ Helpers

    private void EnsureAuthenticated()
    {
        if (!_authService.IsAuthenticated || _authService.IdToken is null || _authService.CurrentUser is null)
            throw new InvalidOperationException("User is not authenticated. Sign in before syncing.");
    }

    private void AttachAuth(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authService.IdToken);
    }

    private HttpRequestMessage CreatePatchRequest(string url, string jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        AttachAuth(request);
        return request;
    }

    /// <summary>
    /// Executes an HTTP request. On 401, attempts a token refresh and retries once.
    /// </summary>
    private async Task<HttpResponseMessage> ExecuteWithRetryAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Received 401; attempting token refresh");
            await _authService.RefreshTokenAsync().ConfigureAwait(false);

            // Clone isn't possible after send; build a minimal retry.
            using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
            AttachAuth(retry);
            if (request.Content is not null)
            {
                var bodyBytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                retry.Content = new ByteArrayContent(bodyBytes);
                retry.Content.Headers.ContentType = request.Content.Headers.ContentType;
            }

            response = await _http.SendAsync(retry, ct).ConfigureAwait(false);
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    // ------------------------------------------------------------------ Firestore field helpers

    private string SnippetDocPath(string snippetId) =>
        $"{FirebaseConfig.FirestoreBaseUrl}/users/{_authService.CurrentUser!.UserId}/snippets/{snippetId}";

    private string SettingsDocPath() =>
        $"{FirebaseConfig.FirestoreBaseUrl}/users/{_authService.CurrentUser!.UserId}/settings/app";

    private static string SnippetToFirestoreFields(Snippet s)
    {
        var fields = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["name"] = StringValue(s.Name),
                ["content"] = StringValue(s.Content),
                ["textShortcut"] = StringValue(s.TextShortcut),
                ["hotkey"] = StringValue(s.Hotkey),
                ["isEnabled"] = BoolValue(s.IsEnabled),
                ["createdAt"] = TimestampValue(s.CreatedAt),
                ["updatedAt"] = TimestampValue(s.UpdatedAt)
            }
        };
        return fields.ToJsonString();
    }

    private static Snippet? FirestoreFieldsToSnippet(JsonNode doc)
    {
        var fields = doc["fields"];
        if (fields is null) return null;

        // Extract document ID from the full resource name
        var name = doc["name"]?.GetValue<string>() ?? string.Empty;
        var docId = name.Split('/').LastOrDefault() ?? Guid.NewGuid().ToString();

        return new Snippet
        {
            Id = docId,
            Name = GetStringField(fields, "name"),
            Content = GetStringField(fields, "content"),
            TextShortcut = GetStringField(fields, "textShortcut"),
            Hotkey = GetStringField(fields, "hotkey"),
            IsEnabled = GetBoolField(fields, "isEnabled"),
            CreatedAt = GetTimestampField(fields, "createdAt"),
            UpdatedAt = GetTimestampField(fields, "updatedAt")
        };
    }

    private static string SettingsToFirestoreFields(AppSettings s)
    {
        var fields = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["historyLimit"] = IntValue(s.HistoryLimit),
                ["startupOnBoot"] = BoolValue(s.StartupOnBoot),
                ["formattingMode"] = StringValue(s.FormattingMode.ToString()),
                ["clipboardTrayHotkey"] = StringValue(s.ClipboardTrayHotkey),
                ["snippetPickerHotkey"] = StringValue(s.SnippetPickerHotkey),
                ["pasteLastHotkey"] = StringValue(s.PasteLastHotkey),
                ["dateFormat"] = StringValue(s.DateFormat),
                ["timeFormat"] = StringValue(s.TimeFormat),
                ["delimiterTriggers"] = StringValue(s.DelimiterTriggers)
            }
        };
        return fields.ToJsonString();
    }

    private static AppSettings FirestoreFieldsToSettings(JsonNode doc)
    {
        var fields = doc["fields"];
        if (fields is null) return new AppSettings();

        var settings = new AppSettings
        {
            HistoryLimit = GetIntField(fields, "historyLimit", 500),
            StartupOnBoot = GetBoolField(fields, "startupOnBoot"),
            ClipboardTrayHotkey = GetStringField(fields, "clipboardTrayHotkey", "Ctrl+Shift+V"),
            SnippetPickerHotkey = GetStringField(fields, "snippetPickerHotkey", "Ctrl+Shift+S"),
            PasteLastHotkey = GetStringField(fields, "pasteLastHotkey", "Ctrl+Shift+Z"),
            DateFormat = GetStringField(fields, "dateFormat", "yyyy-MM-dd"),
            TimeFormat = GetStringField(fields, "timeFormat", "HH:mm:ss"),
            DelimiterTriggers = GetStringField(fields, "delimiterTriggers", " \t\n.,;:!?()[]{}")
        };

        if (Enum.TryParse<FormattingMode>(GetStringField(fields, "formattingMode"), out var fm))
            settings.FormattingMode = fm;

        return settings;
    }

    private static UserProfile ParseUserProfile(JsonNode doc)
    {
        var fields = doc["fields"];
        if (fields is null) return new UserProfile();

        var profile = new UserProfile
        {
            Email = GetStringField(fields, "email"),
            TrialStart = GetNullableTimestampField(fields, "trialStart"),
            TrialEnd = GetNullableTimestampField(fields, "trialEnd")
        };

        // Extract userId from document name path
        var name = doc["name"]?.GetValue<string>() ?? string.Empty;
        var userId = name.Split('/').LastOrDefault() ?? string.Empty;
        profile.UserId = userId;

        if (Enum.TryParse<SubscriptionStatus>(GetStringField(fields, "subscriptionStatus"), out var ss))
            profile.SubscriptionStatus = ss;

        if (Enum.TryParse<PlanType>(GetStringField(fields, "planType"), out var pt))
            profile.PlanType = pt;

        return profile;
    }

    // ------------------------------------------------------------------ Firestore value constructors

    private static JsonObject StringValue(string v) => new() { ["stringValue"] = v };
    private static JsonObject BoolValue(bool v) => new() { ["booleanValue"] = v };
    private static JsonObject IntValue(int v) => new() { ["integerValue"] = v.ToString() };
    private static JsonObject TimestampValue(DateTime v) =>
        new() { ["timestampValue"] = v.ToUniversalTime().ToString("o") };

    // ------------------------------------------------------------------ Firestore field extractors

    private static string GetStringField(JsonNode fields, string key, string fallback = "")
    {
        return fields[key]?["stringValue"]?.GetValue<string>() ?? fallback;
    }

    private static bool GetBoolField(JsonNode fields, string key, bool fallback = false)
    {
        return fields[key]?["booleanValue"]?.GetValue<bool>() ?? fallback;
    }

    private static int GetIntField(JsonNode fields, string key, int fallback = 0)
    {
        var raw = fields[key]?["integerValue"]?.GetValue<string>();
        return raw is not null && int.TryParse(raw, out var v) ? v : fallback;
    }

    private static DateTime GetTimestampField(JsonNode fields, string key)
    {
        var raw = fields[key]?["timestampValue"]?.GetValue<string>();
        return raw is not null && DateTime.TryParse(raw, out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;
    }

    private static DateTime? GetNullableTimestampField(JsonNode fields, string key)
    {
        var raw = fields[key]?["timestampValue"]?.GetValue<string>();
        if (raw is not null && DateTime.TryParse(raw, out var dt))
            return dt.ToUniversalTime();
        return null;
    }
}
