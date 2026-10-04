using System.Text.Json;

namespace FrameForge.Desktop.Assets;

/// <summary>Machine-local asset settings; deliberately separate from portable project JSON.</summary>
public sealed class AssetSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public AssetSettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameForge", "settings.json");
    }

    public string Path { get; }

    public IReadOnlyList<string> Load()
        => LoadConfiguration().AssetRoots;

    public FrameForgeLocalSettings LoadConfiguration()
    {
        try
        {
            if (!File.Exists(Path))
                return new FrameForgeLocalSettings();
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path), JsonOptions);
            return new FrameForgeLocalSettings(
                settings?.AssetRoots?.Where(root => !string.IsNullOrWhiteSpace(root)).ToArray() ?? [],
                string.IsNullOrWhiteSpace(settings?.WowClientPath) ? null : settings.WowClientPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new FrameForgeLocalSettings();
        }
    }

    public void Save(IEnumerable<string> roots)
    {
        var current = LoadConfiguration();
        SaveConfiguration(current with { AssetRoots = [.. roots] });
    }

    public void SaveConfiguration(FrameForgeLocalSettings configuration)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings
        {
            AssetRoots = [.. configuration.AssetRoots],
            WowClientPath = configuration.WowClientPath,
        }, JsonOptions));
        File.Move(temporary, Path, overwrite: true);
    }

    private sealed class Settings
    {
        public string[] AssetRoots { get; set; } = [];
        public string? WowClientPath { get; set; }
    }
}

public sealed record FrameForgeLocalSettings(
    IReadOnlyList<string>? Roots = null,
    string? WowClientPath = null)
{
    public IReadOnlyList<string> AssetRoots { get; init; } = Roots ?? [];
}
