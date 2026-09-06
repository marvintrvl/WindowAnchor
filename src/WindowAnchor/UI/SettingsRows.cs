using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.UI;

/// <summary>Binding row model used by the Settings workspace section.</summary>
internal sealed class WorkspaceRow : INotifyPropertyChanged
{
    public WorkspaceSnapshot Source { get; init; } = null!;
    public string Name => Source.Name;
    public string FingerprintLabel => Source.MonitorFingerprint;
    public string SavedAtDisplay => Source.SavedAt.ToLocalTime().ToString("g");
    public int EntryCount => Source.Entries.Count;
    public string MonitorCountLabel => Source.Monitors.Count > 0 ? $"{Source.Monitors.Count}" : "—";
    public string SavedWithFilesLabel => Source.SavedWithFiles ? "Yes" : "—";

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

    private bool _isEditing;
    private string _editName = "";
    public bool IsEditing { get => _isEditing; set { _isEditing = value; OnPropertyChanged(); } }
    public string EditName { get => _editName; set { _editName = value; OnPropertyChanged(); } }

    private int _position;
    public int Position
    {
        get => _position;
        set { _position = value; OnPropertyChanged(); OnPropertyChanged(nameof(SlotLabel)); OnPropertyChanged(nameof(SlotBadgeVisibility)); }
    }
    public string SlotLabel => $"#{_position}";
    public Visibility SlotBadgeVisibility => _position <= 3 ? Visibility.Visible : Visibility.Collapsed;

    private bool _isDefault;
    public bool IsDefault
    {
        get => _isDefault;
        set { _isDefault = value; OnPropertyChanged(); OnPropertyChanged(nameof(DefaultStarVisibility)); }
    }
    public Visibility DefaultStarVisibility => _isDefault ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Binding row model used by the Settings hotkey section.</summary>
internal sealed class HotkeyRow : INotifyPropertyChanged
{
    public string ActionId { get; init; } = "";
    public string ActionName { get; init; } = "";

    private ModifierKeys _modifiers;
    public ModifierKeys Modifiers
    {
        get => _modifiers;
        set { _modifiers = value; OnPropertyChanged(); DisplayShortcut = HotkeyService.FormatShortcut(value, _key); }
    }

    private Key _key;
    public Key Key
    {
        get => _key;
        set { _key = value; OnPropertyChanged(); DisplayShortcut = HotkeyService.FormatShortcut(_modifiers, value); }
    }

    private string _displayShortcut = "";
    public string DisplayShortcut
    {
        get => _displayShortcut;
        set { _displayShortcut = value; OnPropertyChanged(); }
    }

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        set { _isRecording = value; OnPropertyChanged(); }
    }

    private bool _isCustom;
    public bool IsCustom
    {
        get => _isCustom;
        set { _isCustom = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResetVisibility)); }
    }
    public Visibility ResetVisibility => _isCustom ? Visibility.Visible : Visibility.Collapsed;

    public ModifierKeys DefaultModifiers { get; init; }
    public Key DefaultKey { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Binding row model used by the Settings monitor section.</summary>
internal sealed class MonitorRow : INotifyPropertyChanged
{
    public string MonitorId { get; init; } = "";
    public string HardwareName { get; init; } = "";
    public string IndexLabel { get; init; } = "";
    public string ResolutionLabel { get; init; } = "";

    private string _alias = "";
    public string Alias
    {
        get => _alias;
        set { _alias = value; OnPropertyChanged(); OnPropertyChanged(nameof(AliasDisplay)); }
    }
    public string AliasDisplay => string.IsNullOrWhiteSpace(_alias) ? "—" : _alias;

    private string _editAlias = "";
    public string EditAlias
    {
        get => _editAlias;
        set { _editAlias = value; OnPropertyChanged(); }
    }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ViewVisibility));
            OnPropertyChanged(nameof(EditVisibility));
        }
    }

    public Visibility ViewVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
