using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DupFind.App.Mvvm;

/// <summary>
/// Minimal INotifyPropertyChanged base. Deliberately no MVVM framework: the pattern is
/// small enough to read in one screen, and an interviewer can ask about any line of it.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
