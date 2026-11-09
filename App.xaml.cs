using System.Windows;
using UpscaleHelper.Infrastructure;
using UpscaleHelper.Localization;
using UpscaleHelper.Presentation;
using UpscaleHelper.Processing;

namespace UpscaleHelper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DiagnosticLog.Write($"UpscaleHelper {typeof(App).Assembly.GetName().Version} started.");
        DispatcherUnhandledException += (_, args) => DiagnosticLog.Write($"UI error: {args.Exception.GetType().Name} ({args.Exception.HResult:X8}).");
        _ = Task.Run(() => TemporaryFolder.Cleanup());
        var settings = new SettingsStore(AppStorage.Root);
        var language = new InterfaceLanguage(Resources, settings);
        var paths = new ToolPaths(AppContext.BaseDirectory);
        var runner = new ToolRunner();
        var media = new MediaTools(paths, runner);
        var processor = new UpscaleProcessor(media, new FrameUpscaler(paths, runner));
        var queue = new LocalJsonFile<QueueEntry[]>(AppStorage.Root, "queue.json");
        var studio = new StudioViewModel(processor, media, new VideoPreviewBuilder(processor, media), new StudioDialogs(media), language, settings, queue);
        MainWindow = new MainWindow(studio);
        MainWindow.Show();
        studio.AddFiles(e.Args.Where(File.Exists).Select(Path.GetFullPath));
    }
}
