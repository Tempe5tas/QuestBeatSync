using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QuestBeatSync.App.ViewModels;
using QuestBeatSync.App.Views;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Adb;
using QuestBeatSync.Infrastructure.Importing;
using QuestBeatSync.Infrastructure.Scanning;
using QuestBeatSync.Infrastructure.BeatSaver;
using QuestBeatSync.Infrastructure.Cache;
using QuestBeatSync.Infrastructure.Execution;

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
            var transport = new AdbQuestTransport(transportOptions);
            var scanner = new QuestBeatSaberScanner(
                new AdbQuestRemoteFileSystem(transport),
                QuestBeatSaberPaths.Default);
            var beatSaverClient = new BeatSaverClient(new HttpClient());
            var beatMapCache = new LocalBeatMapCache(
                Path.Combine(appDataDirectory, "cache", "maps"),
                beatSaverClient);
            var syncExecutor = new SyncExecutor(
                scanner,
                new LocalPlaylistExecutionWorkspace(Path.Combine(appDataDirectory, "executions")),
                beatMapCache,
                new AdbQuestSyncTarget(transport, QuestBeatSaberPaths.Default),
                new JsonSyncExecutionJournal(Path.Combine(appDataDirectory, "execution-journal")),
                QuestBeatSaberPaths.Default);

            var viewModel = new MainWindowViewModel(
                transport,
                scanner,
                new LocalBplistImporter(),
                beatSaverClient,
                beatMapCache,
                transportOptions,
                settingsStore,
                syncExecutor);
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
