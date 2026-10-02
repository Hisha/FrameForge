using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FrameForge.Desktop.Services;
using Avalonia.Markup.Xaml;
using FrameForge.Desktop.ViewModels;
using FrameForge.Desktop.Views;

namespace FrameForge.Desktop;

public partial class App : Application
{
    /// <summary>The main editor window, once it exists.</summary>
    public static MainWindow? EditorWindow { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The smoke self-check drives this same window, so there is only ever one window
            // implementation to keep working.
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel();
            EditorWindow = window;
            desktop.MainWindow = window;

            // The window exists from here on, so the self-check can be queued. Posting keeps it
            // on the UI thread once the main loop is actually running, and lets it pump layout
            // and render passes before it inspects anything.
            if (SmokeTest.IsRequested)
                Dispatcher.UIThread.Post(() => SmokeTest.Run(desktop), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
