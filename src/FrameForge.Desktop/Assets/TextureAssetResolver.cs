using FrameForge.Core.Models;

namespace FrameForge.Desktop.Assets;

public enum AssetResolutionStatus
{
    Resolved,
    Missing,
    UnsupportedFormat,
    Ambiguous,
    InvalidPath,
    DecodeFailed,
    NoReference,
}

public enum TextureFileFormat { Tga, Blp, Png, Unknown }
public enum AssetSourceKind { SourceRelative, ConfiguredRoot }

public sealed record AssetResolutionDiagnostic(AssetResolutionStatus Status, string Message);

public sealed record ResolvedTextureAsset(
    string? Reference,
    AssetResolutionStatus Status,
    string? PhysicalPath,
    AssetSourceKind? SourceKind,
    string? SourceRoot,
    TextureFileFormat Format,
    DecodedTexture? Texture,
    AssetResolutionDiagnostic Diagnostic)
{
    public int? Width => Texture?.Image.Width;
    public int? Height => Texture?.Image.Height;
    public bool CanRender => Status == AssetResolutionStatus.Resolved && Texture is not null;
}

public interface ITextureAssetResolver
{
    ResolvedTextureAsset Resolve(string? reference);
}

/// <summary>Resolves WoW Interface paths and caches successfully decoded physical assets.</summary>
public sealed class TextureAssetResolver : ITextureAssetResolver, IDisposable
{
    private readonly Dictionary<string, ResolvedTextureAsset> _resolutionCache = new(StringComparer.Ordinal);
    private readonly Dictionary<FileIdentity, DecodedTexture> _decodeCache = [];
    private readonly TextureDecoderRegistry _decoders;
    private string? _sourcePath;
    private string[] _assetRoots = [];

    public int DecodeCount { get; private set; }
    public int CacheHitCount { get; private set; }
    public IReadOnlyList<string> AssetRoots => _assetRoots;
    public string? SourcePath => _sourcePath;

    public TextureAssetResolver(TextureDecoderRegistry? decoders = null) =>
        _decoders = decoders ?? new TextureDecoderRegistry();

    public void Configure(string? sourcePath, IEnumerable<string> assetRoots)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(sourcePath) ? null : NormalizePhysicalPath(sourcePath);
        var normalizedRoots = assetRoots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizePhysicalPath).Distinct(StringComparer.Ordinal).ToArray();
        if (string.Equals(_sourcePath, normalizedSource, StringComparison.Ordinal)
            && _assetRoots.SequenceEqual(normalizedRoots, StringComparer.Ordinal))
            return;

        _sourcePath = normalizedSource;
        _assetRoots = normalizedRoots;
        Refresh();
    }

    public ResolvedTextureAsset Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return Failure(reference, AssetResolutionStatus.NoReference, "No texture file is declared; the visual may be inherited or assigned at runtime.");

        if (_resolutionCache.TryGetValue(reference, out var cached))
        {
            CacheHitCount++;
            return cached;
        }

        var result = ResolveUncached(reference);
        _resolutionCache[reference] = result;
        return result;
    }

    public void Refresh()
    {
        _resolutionCache.Clear();
        foreach (var texture in _decodeCache.Values)
            texture.Dispose();
        _decodeCache.Clear();
        CacheHitCount = 0;
    }

    private ResolvedTextureAsset ResolveUncached(string reference)
    {
        if (!TryNormalize(reference, out var segments, out var invalidReason))
            return Failure(reference, AssetResolutionStatus.InvalidPath, invalidReason!);

        var sources = new List<(string Root, AssetSourceKind Kind)>();
        if (FindContentRoot(_sourcePath) is { } sourceRoot)
            sources.Add((sourceRoot, AssetSourceKind.SourceRelative));
        sources.AddRange(_assetRoots.Select(root => (root, AssetSourceKind.ConfiguredRoot)));

        var invalidRoots = new List<string>();
        foreach (var source in sources)
        {
            if (!Directory.Exists(source.Root))
            {
                invalidRoots.Add(source.Root);
                continue;
            }

            LocateResult located;
            try
            {
                located = Locate(source.Root, segments);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                invalidRoots.Add($"{source.Root} ({ex.Message})");
                continue;
            }
            if (located.Ambiguous.Count > 0)
                return Failure(reference, AssetResolutionStatus.Ambiguous,
                    $"Multiple case/extension candidates exist beneath {source.Root}: {string.Join(", ", located.Ambiguous.Select(Path.GetFileName))}",
                    source.Kind, source.Root);
            if (located.Path is null)
                continue;

            var detectedFormat = TextureFileFormat.Unknown;
            try
            {
                var info = new FileInfo(located.Path);
                var identity = new FileIdentity(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
                if (!_decodeCache.TryGetValue(identity, out var texture))
                {
                    using var stream = File.OpenRead(located.Path);
                    detectedFormat = _decoders.Identify(stream);
                    texture = new DecodedTexture(_decoders.Decode(stream, detectedFormat));
                    _decodeCache[identity] = texture;
                    DecodeCount++;
                }
                else
                {
                    CacheHitCount++;
                    detectedFormat = texture.Image.Format;
                }

                return new ResolvedTextureAsset(reference, AssetResolutionStatus.Resolved, located.Path,
                    source.Kind, source.Root, detectedFormat, texture,
                    new AssetResolutionDiagnostic(AssetResolutionStatus.Resolved, "Resolved and decoded."));
            }
            catch (UnsupportedTextureEncodingException ex)
            {
                return Failure(reference, AssetResolutionStatus.UnsupportedFormat, ex.Message,
                    source.Kind, source.Root, located.Path, detectedFormat);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or OverflowException)
            {
                return Failure(reference, AssetResolutionStatus.DecodeFailed, ex.Message,
                    source.Kind, source.Root, located.Path, detectedFormat);
            }
        }

        var suffix = invalidRoots.Count == 0 ? string.Empty : $" Invalid or missing roots: {string.Join(", ", invalidRoots)}.";
        return Failure(reference, AssetResolutionStatus.Missing,
            $"No matching asset was found in the source hierarchy or configured roots.{suffix}");
    }

    public static bool TryNormalize(string reference, out string[] segments, out string? reason)
    {
        segments = [];
        reason = null;
        var portable = reference.Trim().Replace('\\', '/');
        if (portable.Length == 0 || portable.StartsWith('/') || Path.IsPathRooted(portable) || portable.Contains(':'))
        {
            reason = "Texture references must be relative WoW paths.";
            return false;
        }

        segments = portable.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            reason = "Texture references may not contain traversal segments.";
            return false;
        }
        return true;
    }

    private static string? FindContentRoot(string? sourcePath)
    {
        if (sourcePath is null)
            return null;
        var directory = new FileInfo(sourcePath).Directory;
        while (directory is not null)
        {
            if (directory.Name.Equals("Interface", StringComparison.OrdinalIgnoreCase))
                return directory.Parent?.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static string NormalizePhysicalPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static LocateResult Locate(string root, IReadOnlyList<string> originalSegments)
    {
        var segments = originalSegments;
        if (Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals("Interface", StringComparison.OrdinalIgnoreCase)
            && segments.Count > 0 && segments[0].Equals("Interface", StringComparison.OrdinalIgnoreCase))
            segments = originalSegments.Skip(1).ToArray();

        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var matches = MatchEntries(current, segments[i], directories: true);
            if (matches.Count != 1)
                return matches.Count > 1 ? new(null, matches) : new(null, []);
            current = matches[0];
        }

        var leaf = segments[^1];
        var names = Path.HasExtension(leaf) ? [leaf] : new[] { leaf, leaf + ".tga", leaf + ".blp", leaf + ".png" };
        var candidates = names.SelectMany(name => MatchEntries(current, name, directories: false))
            .Distinct(StringComparer.Ordinal).ToArray();
        return candidates.Length switch
        {
            0 => new(null, []),
            1 => new(candidates[0], []),
            _ => new(null, candidates),
        };
    }

    private static IReadOnlyList<string> MatchEntries(string directory, string name, bool directories)
    {
        if (!Directory.Exists(directory))
            return [];
        var entries = directories ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFiles(directory);
        var all = entries.Where(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = all.Where(path => Path.GetFileName(path).Equals(name, StringComparison.Ordinal)).ToArray();
        return exact.Length > 0 ? exact : all;
    }

    private static ResolvedTextureAsset Failure(string? reference, AssetResolutionStatus status, string message,
        AssetSourceKind? sourceKind = null, string? root = null, string? path = null,
        TextureFileFormat format = TextureFileFormat.Unknown) =>
        new(reference, status, path, sourceKind, root, format, null, new AssetResolutionDiagnostic(status, message));

    public void Dispose() => Refresh();

    private sealed record LocateResult(string? Path, IReadOnlyList<string> Ambiguous);
    private readonly record struct FileIdentity(string Path, long Length, long LastWriteTicks);
}

public static class TextureSourceRect
{
    public static Avalonia.Rect Map(TexCoords coordinates, int imageWidth, int imageHeight)
    {
        if (!coordinates.IsValid || coordinates.Width <= 0 || coordinates.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(coordinates), "TexCoords must be ordered, finite fractions within 0..1.");
        return new Avalonia.Rect(coordinates.Left * imageWidth, coordinates.Top * imageHeight,
            coordinates.Width * imageWidth, coordinates.Height * imageHeight);
    }
}
