using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FrameForge.Desktop.Services;
using FrameForge.Core.Export;
using FrameForge.Core.Serialization;

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
        var exportIndex = Array.IndexOf(args, "--export-wow335");
        if (exportIndex >= 0)
        {
            if (exportIndex + 2 >= args.Length)
            {
                Console.Error.WriteLine("Usage: FrameForge --export-wow335 <project.fforge.json> <destination-directory>");
                return 2;
            }
            try
            {
                var projectPath = Path.GetFullPath(args[exportIndex + 1]);
                var parsed = ProjectCodec.Parse(File.ReadAllText(projectPath));
                if (!parsed.Ok)
                {
                    Console.Error.WriteLine(parsed.ErrorText);
                    return 2;
                }
                var result = Wow335Exporter.Export(parsed.Project!, projectPath, Path.GetFullPath(args[exportIndex + 2]));
                foreach (var diagnostic in result.Diagnostics)
                    Console.WriteLine($"{diagnostic.Severity.ToString().ToUpperInvariant()} {diagnostic.Code}: {diagnostic.Message}");
                Console.WriteLine(result.Summary);
                return result.Success ? 0 : 2;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

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
