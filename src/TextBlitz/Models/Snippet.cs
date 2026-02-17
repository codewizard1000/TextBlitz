using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TextBlitz.Models;

public partial class Snippet : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _textShortcut = string.Empty;

    [ObservableProperty]
    private string _hotkey = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private DateTime _createdAt = DateTime.UtcNow;

    [ObservableProperty]
    private DateTime _updatedAt = DateTime.UtcNow;

    [ObservableProperty]
    private string? _syncId;
}
