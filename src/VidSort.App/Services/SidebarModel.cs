using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VidSort.App.Services;

public sealed class SidebarFolderItem
{
    public string DisplayText { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
}

public sealed class SidebarVideoRow : INotifyPropertyChanged
{
    private bool _isPlaying;
    public string RelativePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value)
            {
                return;
            }

            _isPlaying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
        }
    }
    public string DisplayText => IsPlaying ? "▶ " + FileName : FileName;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
