using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FrameForge.Desktop.Services;
using FrameForge.Core.Export;
using FrameForge.Core.Serialization;
using FrameForge.Core.Viewing;
using System.Text.Json;

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
        var compareIndex = Array.IndexOf(args, "--compare-wow-landmarks");
        if (compareIndex >= 0)
        {
            if (compareIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("Usage: FrameForge --compare-wow-landmarks <landmarks.json> [report.json]");
                return 2;
            }
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                };
                var samples = JsonSerializer.Deserialize<VisualLandmarkSample[]>(
                    File.ReadAllText(Path.GetFullPath(args[compareIndex + 1])), options)
                    ?? throw new InvalidDataException("The landmark file did not contain an array.");
                var report = VisualLandmarkComparison.Compare(samples);
                var json = JsonSerializer.Serialize(report, options);
                if (compareIndex + 2 < args.Length && !args[compareIndex + 2].StartsWith("--", StringComparison.Ordinal))
                    File.WriteAllText(Path.GetFullPath(args[compareIndex + 2]), json);
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException or JsonException or ArgumentException)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

        var exportIndex = Array.IndexOf(args, "--export-wow335");
        if (exportIndex >= 0)
        {
            if (exportIndex + 2 >= args.Length)
            {
                Console.Error.WriteLine("Usage: FrameForge --export-wow335 <project.fforge.json> <destination-directory> [--texture-format blp2|tga] [--preserve-source-resolution]");
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
                var formatIndex = Array.IndexOf(args, "--texture-format");
                var format = formatIndex < 0 || formatIndex + 1 < args.Length && args[formatIndex + 1] == "blp2"
                    ? WowTextureFormat.Blp2Dxt5
                    : formatIndex + 1 < args.Length && args[formatIndex + 1] == "tga"
                        ? WowTextureFormat.Tga
                        : throw new InvalidDataException("--texture-format must be 'blp2' or 'tga'.");
                var options = new Wow335ExportOptions
                {
                    TextureFormat = format,
                    ResolutionPolicy = args.Contains("--preserve-source-resolution", StringComparer.Ordinal)
                        ? WowTextureResolutionPolicy.PreserveSource
                        : WowTextureResolutionPolicy.Conservative,
                };
                var result = Wow335Exporter.Export(parsed.Project!, projectPath,
                    Path.GetFullPath(args[exportIndex + 2]), options);
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
