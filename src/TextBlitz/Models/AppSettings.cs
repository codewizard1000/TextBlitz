using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TextBlitz.Models;

public partial class AppSettings : ObservableObject
{
    [ObservableProperty]
    private int _historyLimit = 500;

    [ObservableProperty]
    private bool _startupOnBoot;

    [ObservableProperty]
    private FormattingMode _formattingMode = FormattingMode.KeepOriginal;

    [ObservableProperty]
    private string _clipboardTrayHotkey = "Ctrl+Shift+V";

    [ObservableProperty]
    private string _snippetPickerHotkey = "Ctrl+Shift+S";

    [ObservableProperty]
    private string _pasteLastHotkey = "Ctrl+Shift+Z";

    [ObservableProperty]
    private string _dateFormat = "yyyy-MM-dd";

    [ObservableProperty]
    private string _timeFormat = "HH:mm:ss";

    [ObservableProperty]
    private string _delimiterTriggers = " \t\n.,;:!?()[]{}";
}
