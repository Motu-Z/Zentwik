using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Zentwik.Desktop.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    private readonly ConcurrentDictionary<string, PropertyChangedEventArgs> _cache = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null)
    {
        if (propertyName is null)
        {
            return;
        }

        var args = _cache.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, args);
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }
}
