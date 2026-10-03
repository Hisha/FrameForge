using System.Text.Json;

namespace FrameForge.Desktop.Assets;

/// <summary>Machine-local asset roots; deliberately separate from portable project JSON.</summary>
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
    {
        try
        {
            if (!File.Exists(Path))
                return [];
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path), JsonOptions);
            return settings?.AssetRoots?.Where(root => !string.IsNullOrWhiteSpace(root)).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<string> roots)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(Path, JsonSerializer.Serialize(new Settings { AssetRoots = [.. roots] }, JsonOptions));
    }

    private sealed class Settings
    {
        public string[] AssetRoots { get; set; } = [];
    }
}
