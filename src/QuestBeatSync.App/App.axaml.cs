using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QuestBeatSync.App.ViewModels;
using QuestBeatSync.App.Views;
using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuestBeatSync");
            var settingsStore = new AdbSettingsStore(Path.Combine(appDataDirectory, "settings.json"));
            var transportOptions = new AdbQuestTransportOptions
            {
                ConfiguredExecutablePath = settingsStore.LoadConfiguredPath(),
                AppDataToolsDirectory = Path.Combine(appDataDirectory, "tools")
            };

            var viewModel = new MainWindowViewModel(
                new AdbQuestTransport(transportOptions),
                transportOptions,
                settingsStore);
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
