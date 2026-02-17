namespace TextBlitz.Services.Billing;

/// <summary>
/// Static Stripe configuration. Replace placeholder values with real
/// Stripe keys and price IDs before shipping.
/// </summary>
public static class StripeConfig
{
    /// <summary>Stripe publishable key (safe to embed in client-side code).</summary>
    public static string PublishableKey { get; set; } = "pk_test_placeholder";

    /// <summary>Stripe Price ID for the monthly Pro plan.</summary>
    public static string MonthlyPriceId { get; set; } = "price_monthly_placeholder";

    /// <summary>Stripe Price ID for the annual Pro plan.</summary>
    public static string AnnualPriceId { get; set; } = "price_annual_placeholder";

    /// <summary>URL the user is redirected to after a successful checkout.</summary>
    public static string SuccessUrl { get; set; } = "https://textblitz.app/checkout/success?session_id={CHECKOUT_SESSION_ID}";

    /// <summary>URL the user is redirected to if they cancel checkout.</summary>
    public static string CancelUrl { get; set; } = "https://textblitz.app/checkout/cancel";

    /// <summary>
    /// Base URL for Stripe Checkout sessions. In production, your backend creates
    /// checkout sessions and returns the URL. This is the backend endpoint to call.
    /// </summary>
    public static string CheckoutBaseUrl { get; set; } = "https://api.textblitz.app/create-checkout-session";
}
