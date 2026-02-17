using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TextBlitz.Models;

public partial class UserProfile : ObservableObject
{
    [ObservableProperty]
    private string _userId = string.Empty;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private DateTime? _trialStart;

    [ObservableProperty]
    private DateTime? _trialEnd;

    [ObservableProperty]
    private SubscriptionStatus _subscriptionStatus = SubscriptionStatus.None;

    [ObservableProperty]
    private PlanType _planType = PlanType.Free;
}

public enum SubscriptionStatus
{
    None,
    Trial,
    Active,
    PastDue,
    Canceled,
    Expired
}

public enum PlanType
{
    Free,
    ProMonthly,
    ProAnnual
}
