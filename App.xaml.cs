using System.Windows;
using UpscaleHelper.Localization;
using UpscaleHelper.Presentation;
using UpscaleHelper.Processing;

namespace UpscaleHelper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var language = new InterfaceLanguage(Resources);
        var paths = new ToolPaths(AppContext.BaseDirectory);
        var runner = new ToolRunner();
        var media = new MediaTools(paths, runner);
        var processor = new UpscaleProcessor(media, new FrameUpscaler(paths, runner));
        var studio = new StudioViewModel(processor, media, new VideoPreviewBuilder(processor, media), new StudioDialogs(media), language);
        MainWindow = new MainWindow(studio);
        MainWindow.Show();
        studio.AddFiles(e.Args.Where(File.Exists).Select(Path.GetFullPath));
    }
}
