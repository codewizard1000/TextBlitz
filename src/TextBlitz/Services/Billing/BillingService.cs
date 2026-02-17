using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TextBlitz.Models;
using TextBlitz.Services.Firebase;

namespace TextBlitz.Services.Billing;

/// <summary>Defines the limits imposed on free-tier users.</summary>
public sealed class FreeTierLimits
{
    public int MaxSnippets { get; init; } = 20;
    public int MaxHistoryItems { get; init; } = 50;
    public bool CanUseTemplateTokens { get; init; } = false;
}

/// <summary>Defines the limits (effectively unlimited) for Pro users.</summary>
public sealed class ProLimits
{
    public int MaxSnippets { get; init; } = int.MaxValue;
    public int MaxHistoryItems { get; init; } = 500;
    public bool CanUseTemplateTokens { get; init; } = true;
}

/// <summary>
/// Manages subscription state, entitlement checks, and Stripe Checkout integration.
/// </summary>
public sealed class BillingService
{
    private static readonly FreeTierLimits DefaultFreeLimits = new();
    private static readonly ProLimits DefaultProLimits = new();

    private readonly FirebaseAuthService _authService;
    private readonly FirestoreSyncService _firestoreSync;
    private readonly HttpClient _http;
    private readonly ILogger<BillingService> _logger;

    public BillingService(
        FirebaseAuthService authService,
        FirestoreSyncService firestoreSync,
        HttpClient httpClient,
        ILogger<BillingService> logger)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _firestoreSync = firestoreSync ?? throw new ArgumentNullException(nameof(firestoreSync));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised when entitlements change (e.g., trial expired, subscription activated).</summary>
    public event EventHandler<EntitlementsChangedEventArgs>? EntitlementsChanged;

    /// <summary>Current subscription status of the authenticated user.</summary>
    public SubscriptionStatus SubscriptionStatus { get; private set; } = SubscriptionStatus.None;

    /// <summary>Current plan type.</summary>
    public PlanType PlanType { get; private set; } = PlanType.Free;

    /// <summary>Whether the user currently has Pro-level access.</summary>
    public bool IsProUser => _authService.IsPro();

    /// <summary>Number of trial days remaining, or 0 if no trial / trial expired.</summary>
    public int TrialDaysRemaining
    {
        get
        {
            var user = _authService.CurrentUser;
            if (user?.TrialEnd is null)
                return 0;

            var remaining = (user.TrialEnd.Value - DateTime.UtcNow).Days;
            return Math.Max(0, remaining);
        }
    }

    /// <summary>
    /// Fetches the user profile from Firestore and updates the local entitlement state.
    /// </summary>
    public async Task CheckEntitlementsAsync(CancellationToken ct = default)
    {
        if (!_authService.IsAuthenticated)
        {
            _logger.LogDebug("Not authenticated; skipping entitlement check");
            return;
        }

        try
        {
            var profile = await _firestoreSync.GetUserProfileAsync(ct).ConfigureAwait(false);
            if (profile is null)
            {
                _logger.LogWarning("User profile not found in Firestore");
                return;
            }

            _authService.UpdateUserProfile(profile);

            var previousStatus = SubscriptionStatus;
            var previousPlan = PlanType;

            SubscriptionStatus = profile.SubscriptionStatus;
            PlanType = profile.PlanType;

            if (previousStatus != SubscriptionStatus || previousPlan != PlanType)
            {
                _logger.LogInformation(
                    "Entitlements changed: {OldStatus}/{OldPlan} -> {NewStatus}/{NewPlan}",
                    previousStatus, previousPlan, SubscriptionStatus, PlanType);

                OnEntitlementsChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check entitlements");
        }
    }

    /// <summary>
    /// Constructs a Stripe Checkout URL for the given plan type.
    /// In production, this calls your backend to create a Checkout Session.
    /// </summary>
    public async Task<string?> GetCheckoutUrlAsync(string planType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planType);

        if (!_authService.IsAuthenticated || _authService.CurrentUser is null)
        {
            _logger.LogWarning("Cannot get checkout URL: user not authenticated");
            return null;
        }

        var priceId = planType.Equals("annual", StringComparison.OrdinalIgnoreCase)
            ? StripeConfig.AnnualPriceId
            : StripeConfig.MonthlyPriceId;

        try
        {
            var payload = new JsonObject
            {
                ["priceId"] = priceId,
                ["userId"] = _authService.CurrentUser.UserId,
                ["email"] = _authService.CurrentUser.Email,
                ["successUrl"] = StripeConfig.SuccessUrl,
                ["cancelUrl"] = StripeConfig.CancelUrl
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, StripeConfig.CheckoutBaseUrl)
            {
                Content = new StringContent(
                    payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authService.IdToken);

            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = JsonNode.Parse(body);
            var checkoutUrl = result?["url"]?.GetValue<string>();

            _logger.LogInformation("Checkout URL generated for plan {Plan}", planType);
            return checkoutUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create checkout session for plan {Plan}", planType);
            return null;
        }
    }

    /// <summary>
    /// Called after a successful Stripe Checkout. Refreshes entitlements from Firestore
    /// (the webhook on the backend will have updated the user document).
    /// </summary>
    public async Task HandleCheckoutCompleteAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        _logger.LogInformation("Checkout completed with session {SessionId}; refreshing entitlements", sessionId);

        // Give the webhook a moment to process, then refresh.
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        await CheckEntitlementsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the applicable tier limits based on current entitlements.
    /// </summary>
    public FreeTierLimits EnforceFreeTierLimits()
    {
        if (IsProUser)
        {
            // Return a FreeTierLimits-shaped object with Pro values so callers
            // do not need to know about ProLimits directly.
            return new FreeTierLimits
            {
                MaxSnippets = DefaultProLimits.MaxSnippets,
                MaxHistoryItems = DefaultProLimits.MaxHistoryItems,
                CanUseTemplateTokens = DefaultProLimits.CanUseTemplateTokens
            };
        }

        return DefaultFreeLimits;
    }

    private void OnEntitlementsChanged()
    {
        EntitlementsChanged?.Invoke(this, new EntitlementsChangedEventArgs(
            SubscriptionStatus, PlanType, IsProUser, TrialDaysRemaining));
    }
}

/// <summary>Event args for <see cref="BillingService.EntitlementsChanged"/>.</summary>
public sealed class EntitlementsChangedEventArgs : EventArgs
{
    public EntitlementsChangedEventArgs(
        SubscriptionStatus status, PlanType plan, bool isPro, int trialDaysRemaining)
    {
        Status = status;
        Plan = plan;
        IsPro = isPro;
        TrialDaysRemaining = trialDaysRemaining;
    }

    public SubscriptionStatus Status { get; }
    public PlanType Plan { get; }
    public bool IsPro { get; }
    public int TrialDaysRemaining { get; }
}
