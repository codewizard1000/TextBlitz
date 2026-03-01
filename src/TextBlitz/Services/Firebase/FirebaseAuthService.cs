using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TextBlitz.Models;

namespace TextBlitz.Services.Firebase;

/// <summary>
/// Manages Firebase authentication state, token storage via DPAPI,
/// and entitlement checks (trial/subscription status).
/// </summary>
public sealed class FirebaseAuthService : IDisposable
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TextBlitz");

    private static readonly string AuthStateFile = Path.Combine(AppDataDir, "auth.dat");

    private readonly ILogger<FirebaseAuthService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _idToken;
    private UserProfile? _currentUser;
    private bool _isAuthenticated;

    public FirebaseAuthService(ILogger<FirebaseAuthService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        LoadPersistedAuthState();
    }

    /// <summary>Raised when authentication state changes (login, logout, token refresh).</summary>
    public event EventHandler<AuthStateChangedEventArgs>? AuthStateChanged;

    /// <summary>Whether the user is currently authenticated with a valid token.</summary>
    public bool IsAuthenticated => _isAuthenticated;

    /// <summary>The current user profile, or null if not authenticated.</summary>
    public UserProfile? CurrentUser => _currentUser;

    /// <summary>The current Firebase ID token used for API requests.</summary>
    public string? IdToken => _idToken;

    /// <summary>
    /// Sets authentication state after a successful login from the WebView2 login page.
    /// </summary>
    public void SetAuthState(string idToken, string userId, string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        _idToken = idToken;
        _currentUser = new UserProfile
        {
            UserId = userId,
            Email = email,
            SubscriptionStatus = SubscriptionStatus.None,
            PlanType = PlanType.Free
        };
        _isAuthenticated = true;

        PersistAuthState();
        OnAuthStateChanged(isSignedIn: true);

        _logger.LogInformation("Auth state set for user {UserId}", userId);
    }

    /// <summary>
    /// Placeholder for Firebase token refresh. In production this would call
    /// the Firebase token endpoint with the stored refresh token.
    /// </summary>
    public async Task RefreshTokenAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isAuthenticated || _currentUser is null)
            {
                _logger.LogWarning("Cannot refresh token: not authenticated");
                return;
            }

            // TODO: Call https://securetoken.googleapis.com/v1/token?key={apiKey}
            // with grant_type=refresh_token and the stored refresh token.
            // For now this is a no-op placeholder.
            _logger.LogInformation("Token refresh requested (placeholder) for user {UserId}",
                _currentUser.UserId);

            await Task.CompletedTask;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Signs out the current user, clears all auth state, and removes persisted data.
    /// </summary>
    public void SignOut()
    {
        _idToken = null;
        _currentUser = null;
        _isAuthenticated = false;

        DeletePersistedAuthState();
        OnAuthStateChanged(isSignedIn: false);

        _logger.LogInformation("User signed out");
    }

    /// <summary>
    /// Reads subscription state from the current user profile and checks trial expiry.
    /// Updates the local user profile accordingly.
    /// </summary>
    public void CheckEntitlements()
    {
        if (_currentUser is null)
        {
            _logger.LogDebug("No current user; skipping entitlement check");
            return;
        }

        if (_currentUser.SubscriptionStatus == SubscriptionStatus.Active)
        {
            _logger.LogDebug("User has active subscription");
            return;
        }

        if (_currentUser.TrialEnd.HasValue && _currentUser.TrialEnd.Value > DateTime.UtcNow)
        {
            _currentUser.SubscriptionStatus = SubscriptionStatus.Trial;
            _logger.LogDebug("Trial active until {TrialEnd}", _currentUser.TrialEnd.Value);
        }
        else if (_currentUser.TrialEnd.HasValue)
        {
            _currentUser.SubscriptionStatus = SubscriptionStatus.Expired;
            _logger.LogDebug("Trial expired at {TrialEnd}", _currentUser.TrialEnd.Value);
        }
    }

    /// <summary>Returns true if the user has an active trial that has not expired.</summary>
    public bool IsTrialActive()
    {
        if (_currentUser?.TrialEnd is null)
            return false;

        return _currentUser.TrialEnd.Value > DateTime.UtcNow;
    }

    /// <summary>
    /// Returns true if the user is a Pro user: either an active subscription
    /// or an active trial period.
    /// </summary>
    public bool IsPro()
    {
        if (_currentUser is null)
            return false;

        return _currentUser.SubscriptionStatus == SubscriptionStatus.Active
               || IsTrialActive();
    }

    /// <summary>
    /// Updates the current user profile with data fetched from Firestore.
    /// Call this after pulling the user document from the server.
    /// </summary>
    public void UpdateUserProfile(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _currentUser = profile;
        CheckEntitlements();
        PersistAuthState();
        OnAuthStateChanged(isSignedIn: true);

        _logger.LogInformation("User profile updated for {UserId}", profile.UserId);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    // --- Persistence via Windows DPAPI ---

    private void PersistAuthState()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);

            var payload = new PersistedAuthState
            {
                IdToken = _idToken,
                UserId = _currentUser?.UserId,
                Email = _currentUser?.Email,
                TrialStart = _currentUser?.TrialStart,
                TrialEnd = _currentUser?.TrialEnd,
                SubscriptionStatus = _currentUser?.SubscriptionStatus ?? SubscriptionStatus.None,
                PlanType = _currentUser?.PlanType ?? PlanType.Free
            };

            var json = JsonSerializer.Serialize(payload);
            var plainBytes = Encoding.UTF8.GetBytes(json);
            var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

            File.WriteAllBytes(AuthStateFile, protectedBytes);
            _logger.LogDebug("Auth state persisted to disk");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist auth state");
        }
    }

    private void LoadPersistedAuthState()
    {
        try
        {
            if (!File.Exists(AuthStateFile))
                return;

            var protectedBytes = File.ReadAllBytes(AuthStateFile);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);

            var state = JsonSerializer.Deserialize<PersistedAuthState>(json);
            if (state is null || string.IsNullOrWhiteSpace(state.IdToken))
                return;

            _idToken = state.IdToken;
            _currentUser = new UserProfile
            {
                UserId = state.UserId ?? string.Empty,
                Email = state.Email ?? string.Empty,
                TrialStart = state.TrialStart,
                TrialEnd = state.TrialEnd,
                SubscriptionStatus = state.SubscriptionStatus,
                PlanType = state.PlanType
            };
            _isAuthenticated = true;

            CheckEntitlements();
            _logger.LogInformation("Restored auth state for user {UserId}", _currentUser.UserId);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt auth state; clearing persisted data");
            DeletePersistedAuthState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted auth state");
        }
    }

    private static void DeletePersistedAuthState()
    {
        try
        {
            if (File.Exists(AuthStateFile))
                File.Delete(AuthStateFile);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private void OnAuthStateChanged(bool isSignedIn)
    {
        AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(isSignedIn, _currentUser));
    }

    // --- Internal types ---

    private sealed class PersistedAuthState
    {
        public string? IdToken { get; set; }
        public string? UserId { get; set; }
        public string? Email { get; set; }
        public DateTime? TrialStart { get; set; }
        public DateTime? TrialEnd { get; set; }
        public SubscriptionStatus SubscriptionStatus { get; set; }
        public PlanType PlanType { get; set; }
    }

}

/// <summary>Event args for <see cref="FirebaseAuthService.AuthStateChanged"/>.</summary>
public sealed class AuthStateChangedEventArgs : EventArgs
{
    public AuthStateChangedEventArgs(bool isSignedIn, UserProfile? user)
    {
        IsSignedIn = isSignedIn;
        User = user;
    }

    public bool IsSignedIn { get; }
    public UserProfile? User { get; }
}
