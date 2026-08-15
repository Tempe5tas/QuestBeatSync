using System.Windows.Input;
using QuestBeatSync.App.ViewModels;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AsyncRelayCommandTests
{
    [TestMethod]
    public async Task ExecuteAsync_ReportsUnexpectedExceptionAndRestoresCommandState()
    {
        Exception? reported = null;
        var command = new AsyncRelayCommand(
            () => throw new IOException("disk full"),
            errorHandler: exception =>
            {
                reported = exception;
                return Task.CompletedTask;
            });

        await command.ExecuteAsync();

        Assert.IsInstanceOfType<IOException>(reported);
        Assert.AreEqual("disk full", command.LastError?.Message);
        Assert.IsFalse(command.IsExecuting);
        Assert.IsTrue(command.CanExecute(null));
    }

    [TestMethod]
    public async Task ExecuteAsync_TreatsCancellationAsNormalCompletion()
    {
        var errorWasReported = false;
        var command = new AsyncRelayCommand(
            () => Task.FromCanceled(new CancellationToken(canceled: true)),
            errorHandler: _ =>
            {
                errorWasReported = true;
                return Task.CompletedTask;
            });

        await command.ExecuteAsync();

        Assert.IsFalse(errorWasReported);
        Assert.IsNull(command.LastError);
        Assert.IsFalse(command.IsExecuting);
    }

    [TestMethod]
    public async Task Execute_ErrorHandlerFailureDoesNotEscapeCommandBoundary()
    {
        var errorHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ICommand command = new AsyncRelayCommand(
            () => throw new InvalidDataException("bad data"),
            errorHandler: _ =>
            {
                errorHandled.TrySetResult();
                throw new InvalidOperationException("notification failed");
            });

        command.Execute(null);

        await errorHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
