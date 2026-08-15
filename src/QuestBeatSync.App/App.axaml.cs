using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QuestBeatSync.App.ViewModels;
using QuestBeatSync.App.Views;
using QuestBeatSync.Infrastructure.Fakes;

namespace QuestBeatSync.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(new FakeQuestTransport())
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

