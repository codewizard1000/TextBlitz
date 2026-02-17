using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TextBlitz.Models;

public partial class ClipboardItem : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _plainText = string.Empty;

    [ObservableProperty]
    private string? _richText;

    [ObservableProperty]
    private string? _htmlText;

    [ObservableProperty]
    private string? _sourceApp;

    [ObservableProperty]
    private DateTime _timestamp = DateTime.UtcNow;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private int _pinOrder;

    [ObservableProperty]
    private int? _listId;
}
