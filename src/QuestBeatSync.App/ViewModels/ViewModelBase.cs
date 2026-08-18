using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace QuestBeatSync.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    private readonly IUiThreadDispatcher _uiDispatcher = UiThreadDispatcher.Current;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected Task OnUiThreadAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return _uiDispatcher.InvokeAsync(action);
    }
}

internal interface IUiThreadDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Action action);
}

internal static class UiThreadDispatcher
{
    public static IUiThreadDispatcher Current { get; set; } = new ImmediateUiThreadDispatcher();
}

internal sealed class AvaloniaUiThreadDispatcher : IUiThreadDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}

internal sealed class ImmediateUiThreadDispatcher : IUiThreadDispatcher
{
    public bool CheckAccess() => true;
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}
