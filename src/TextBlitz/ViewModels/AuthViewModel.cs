using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TextBlitz.Data;
using TextBlitz.Models;

namespace TextBlitz.ViewModels;

/// <summary>
/// ViewModel for the authentication and billing WebView2 window.
/// Manages login, sign-up, subscription upgrade flows, and processes
/// messages received from the embedded WebView2 content.
/// </summary>
public partial class AuthViewModel : ObservableObject
{
    private readonly FirebaseAuthService _authService;
    private readonly BillingService _billingService;
    private readonly DatabaseService _databaseService;

    [ObservableProperty]
    private bool _isAuthenticated;

    [ObservableProperty]
    private string _userEmail = string.Empty;

    [ObservableProperty]
    private bool _isPro;

    [ObservableProperty]
    private int _trialDaysRemaining;

    [ObservableProperty]
    private bool _showUpgradePrompt;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Raised when the auth window should be closed (e.g., after successful login).
    /// </summary>
    public event EventHandler? CloseRequested;

    public AuthViewModel(
        FirebaseAuthService authService,
        BillingService billingService,
        DatabaseService databaseService)
    {
        _authService = authService;
        _billingService = billingService;
        _databaseService = databaseService;

        _authService.AuthStateChanged += OnAuthStateChanged;
        _billingService.SubscriptionChanged += OnSubscriptionChanged;

        RefreshAuthState();
    }

    private void RefreshAuthState()
    {
        IsAuthenticated = _authService.IsAuthenticated;

        if (IsAuthenticated && _authService.CurrentUser is { } user)
        {
            UserEmail = user.Email;
            IsPro = _billingService.IsPro;

            if (user.TrialEnd.HasValue)
            {
                var remaining = (user.TrialEnd.Value - DateTime.UtcNow).Days;
                TrialDaysRemaining = Math.Max(0, remaining);
            }
            else
            {
                TrialDaysRemaining = 0;
            }

            // Show upgrade prompt for trial users and non-pro users
            ShowUpgradePrompt = !IsPro &&
                user.SubscriptionStatus is SubscriptionStatus.Trial or SubscriptionStatus.None;
        }
        else
        {
            UserEmail = string.Empty;
            IsPro = false;
            TrialDaysRemaining = 0;
            ShowUpgradePrompt = false;
        }
    }

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(RefreshAuthState);
    }

    private void OnSubscriptionChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsPro = _billingService.IsPro;
            ShowUpgradePrompt = !IsPro;
        });
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await _authService.SignInAsync();
            RefreshAuthState();

            if (IsAuthenticated)
            {
                await PersistUserProfileAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Login failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"AuthViewModel.LoginAsync error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignUpAsync()
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await _authService.SignUpAsync();
            RefreshAuthState();

            if (IsAuthenticated)
            {
                await PersistUserProfileAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Sign up failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"AuthViewModel.SignUpAsync error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpgradeAsync(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType))
            return;

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await _billingService.InitiateCheckoutAsync(planType);
            RefreshAuthState();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Upgrade failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"AuthViewModel.UpgradeAsync error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await _authService.SignOutAsync();
            RefreshAuthState();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Sign out failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"AuthViewModel.SignOutAsync error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Processes JSON messages received from the WebView2 content.
    /// Expected message format: { "type": "...", "payload": { ... } }
    /// </summary>
    public async void HandleWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                return;

            var messageType = typeElement.GetString();

            switch (messageType)
            {
                case "auth_success":
                    await HandleAuthSuccessAsync(root);
                    break;

                case "auth_error":
                    HandleAuthError(root);
                    break;

                case "checkout_success":
                    await HandleCheckoutSuccessAsync(root);
                    break;

                case "checkout_cancel":
                    // User canceled checkout - no action needed
                    break;

                case "sign_out":
                    await SignOutAsync();
                    break;

                case "close":
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    break;

                default:
                    System.Diagnostics.Debug.WriteLine(
                        $"AuthViewModel.HandleWebMessage: Unknown message type '{messageType}'");
                    break;
            }
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AuthViewModel.HandleWebMessage JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AuthViewModel.HandleWebMessage error: {ex.Message}");
        }
    }

    private async Task HandleAuthSuccessAsync(JsonElement root)
    {
        if (root.TryGetProperty("payload", out var payload))
        {
            var idToken = payload.TryGetProperty("idToken", out var tokenEl)
                ? tokenEl.GetString()
                : null;

            if (!string.IsNullOrEmpty(idToken))
            {
                await _authService.AuthenticateWithTokenAsync(idToken);
            }
        }

        RefreshAuthState();

        if (IsAuthenticated)
        {
            await PersistUserProfileAsync();
        }
    }

    private void HandleAuthError(JsonElement root)
    {
        var message = "Authentication failed.";

        if (root.TryGetProperty("payload", out var payload) &&
            payload.TryGetProperty("message", out var msgEl))
        {
            message = msgEl.GetString() ?? message;
        }

        ErrorMessage = message;
    }

    private async Task HandleCheckoutSuccessAsync(JsonElement root)
    {
        // Refresh billing state after successful checkout
        await _billingService.RefreshEntitlementsAsync();
        RefreshAuthState();

        if (IsAuthenticated)
        {
            await PersistUserProfileAsync();
        }
    }

    private async Task PersistUserProfileAsync()
    {
        if (_authService.CurrentUser is not { } user)
            return;

        var profile = new UserProfile
        {
            UserId = user.UserId,
            Email = user.Email,
            TrialStart = user.TrialStart,
            TrialEnd = user.TrialEnd,
            SubscriptionStatus = user.SubscriptionStatus,
            PlanType = user.PlanType
        };

        await _databaseService.SaveUserProfileAsync(profile);
    }
}
