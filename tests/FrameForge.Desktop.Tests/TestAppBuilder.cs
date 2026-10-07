using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;

namespace FrameForge.Desktop.Tests;

/// <summary>
/// Off-screen Skia host for the pixel-level render tests. The production render pipeline is
/// executed headlessly, so a StatusBar's actual painted pixels can be asserted rather than only
/// the model fraction.
/// </summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                ShouldRenderOnUIThread = true,
                Fps = 60,
                UseHeadlessDrawing = false, // real Skia surfaces so frames are capturable
            });
}