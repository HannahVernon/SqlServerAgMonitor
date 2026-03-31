using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SqlAgMonitor.Core.Models;

/// <summary>
/// Lightweight base class for Core models that need property-change notification.
/// Replaces ReactiveObject dependency with standard <see cref="INotifyPropertyChanged"/>,
/// keeping the Core layer free of UI framework references. Avalonia data binding and
/// ReactiveUI's WhenAnyValue both work with plain INPC.
/// </summary>
public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
