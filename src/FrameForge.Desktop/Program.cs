using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FrameForge.Desktop.Services;

namespace FrameForge.Desktop;

/// <summary>
/// Process entry point.
/// </summary>
/// <remarks>
/// <c>--smoke</c> (or <c>FRAMEFORGE_SMOKE=1</c>) runs the built-in self-check instead of
/// leaving the editor open. See <see cref="SmokeTest"/>.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var smoke = args.Contains("--smoke", StringComparer.Ordinal) ||
                    Environment.GetEnvironmentVariable("FRAMEFORGE_SMOKE") is "1";

        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

        // In smoke mode App queues the self-check once the real window exists; see
        // SmokeTest.IsRequested.
        SmokeTest.IsRequested = smoke;

        return builder.StartWithClassicDesktopLifetime(args);
    }
}
