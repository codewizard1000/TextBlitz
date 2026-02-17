using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TextBlitz.Models;

public partial class SavedList : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private DateTime _createdAt = DateTime.UtcNow;
}
