using System.Windows.Input;

namespace QuestBeatSync.App.ViewModels;

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Func<Exception, Task>? errorHandler = null) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => _isExecuting;

    public Exception? LastError { get; private set; }

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public void Execute(object? parameter) => _ = ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        LastError = null;
        _isExecuting = true;

        try
        {
            RaiseCanExecuteChanged();
            await execute();
        }
        catch (OperationCanceledException)
        {
            // User cancellation is a normal completion path, not an operation error.
        }
        catch (Exception exception)
        {
            await HandleErrorAsync(exception);
        }
        finally
        {
            _isExecuting = false;
            try
            {
                RaiseCanExecuteChanged();
            }
            catch (Exception exception)
            {
                await HandleErrorAsync(exception);
            }
        }
    }

    private async Task HandleErrorAsync(Exception exception)
    {
        LastError = exception;
        if (errorHandler is null)
        {
            return;
        }

        try
        {
            await errorHandler(exception);
        }
        catch
        {
            // An error notification failure must not escape to the UI synchronization context.
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
