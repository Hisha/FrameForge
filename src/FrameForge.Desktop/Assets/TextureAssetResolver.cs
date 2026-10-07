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
public enum AssetSourceKind { ProjectRelative, SourceRelative, ConfiguredRoot }

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

/// <summary>
/// Resolves explicitly identified project design assets or strict WoW Interface paths and caches
/// successfully decoded physical assets. The two namespaces are never inferred from one another.
/// </summary>
public sealed class TextureAssetResolver : ITextureAssetResolver, IDisposable
{
    private readonly Dictionary<string, ResolvedTextureAsset> _resolutionCache = new(StringComparer.Ordinal);
    private readonly Dictionary<FileIdentity, DecodedTexture> _decodeCache = [];
    private readonly TextureDecoderRegistry _decoders;
    private string? _sourcePath;
    private string? _projectRoot;
    private string[] _assetRoots = [];
    private HashSet<string> _designReferences = new(StringComparer.Ordinal);
    private IWoWClientAssetProvider? _stockProvider;
    private WowClientValidation _stockClient = new(WowClientValidationStatus.NotConfigured, null, null, null, [],
        "No WoW client is configured.");

    public int DecodeCount { get; private set; }
    public int CacheHitCount { get; private set; }
    public IReadOnlyList<string> AssetRoots => _assetRoots;
    public string? SourcePath => _sourcePath;

    public TextureAssetResolver(TextureDecoderRegistry? decoders = null) =>
        _decoders = decoders ?? new TextureDecoderRegistry();

    public void Configure(string? sourcePath, IEnumerable<string> assetRoots, string? projectPath = null,
        IEnumerable<string>? designReferences = null)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(sourcePath) ? null : NormalizePhysicalPath(sourcePath);
        var normalizedProjectRoot = string.IsNullOrWhiteSpace(projectPath)
            ? null
            : Path.GetDirectoryName(NormalizePhysicalPath(projectPath));
        var normalizedRoots = assetRoots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizePhysicalPath).Distinct(StringComparer.Ordinal).ToArray();
        var normalizedDesignReferences = (designReferences ?? []).Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim().Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (string.Equals(_sourcePath, normalizedSource, StringComparison.Ordinal)
            && string.Equals(_projectRoot, normalizedProjectRoot, StringComparison.Ordinal)
            && _assetRoots.SequenceEqual(normalizedRoots, StringComparer.Ordinal))
        {
            if (_designReferences.SetEquals(normalizedDesignReferences))
                return;
        }

        _sourcePath = normalizedSource;
        _projectRoot = normalizedProjectRoot;
        _assetRoots = normalizedRoots;
        _designReferences = normalizedDesignReferences;
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

    /// <summary>
    /// Declares the authoritative machine-local stock source for logical WoW references: the
    /// configured WoW client and its managed cache. Resolution of <c>Interface/...</c> and
    /// <c>Fonts/...</c> references consults the cache first and, on a miss, materializes the
    /// reference through the configured client before falling back to legacy sources.
    /// </summary>
    public void ConfigureWowAssetSource(IWoWClientAssetProvider provider, WowClientValidation validation)
    {
        if (ReferenceEquals(_stockProvider, provider) && _stockClient == validation)
            return;
        _stockProvider = provider;
        _stockClient = validation;
        Refresh();
    }

    private ResolvedTextureAsset ResolveUncached(string reference)
    {
        var portableReference = reference.Trim().Replace('\\', '/');
        if (_designReferences.Contains(portableReference))
            return ResolveDesignAsset(portableReference);

        if (!TryNormalize(reference, out var segments, out var invalidReason))
            return Failure(reference, AssetResolutionStatus.InvalidPath, invalidReason!);

        var stockRoot = _stockProvider is null ? null : NormalizePhysicalPath(_stockProvider.CacheRoot);
        var invalidRoots = new List<string>();

        // Priority 1: the managed stock cache, which mirrors the authoritative configured client.
        if (stockRoot is not null)
        {
            var cached = ResolveFromSources(reference, segments,
                [(stockRoot, AssetSourceKind.ConfiguredRoot)], invalidRoots);
            if (cached is not null)
                return cached;
        }

        // Priority 2: the configured WoW client. A normal rendering request for a valid logical
        // asset populates the cache through the existing safe extractor, once; later resolves hit
        // the cache instead of re-reading the MPQs.
        string? clientDiagnostic = null;
        if (stockRoot is not null && _stockClient.IsValid && IsStockWoWReference(segments))
        {
            var materialized = MaterializeFromClient(reference);
            if (materialized is not null)
            {
                clientDiagnostic = materialized.Message;
                if (materialized.Success)
                {
                    var extracted = ResolveFromSources(reference, segments,
                        [(stockRoot, AssetSourceKind.ConfiguredRoot)], invalidRoots);
                    if (extracted is not null)
                        return extracted;
                }
            }
        }

        // Priority 3: the source-relative content hierarchy (an imported layout extracted from an
        // Interface tree) and legacy manual asset roots. These are advanced, diagnostic, and
        // back-compat sources; they can never shadow project-owned artwork, which resolves above.
        var legacy = new List<(string Root, AssetSourceKind Kind)>();
        if (FindContentRoot(_sourcePath) is { } sourceRoot)
            legacy.Add((sourceRoot, AssetSourceKind.SourceRelative));
        legacy.AddRange(_assetRoots.Select(root => (root, AssetSourceKind.ConfiguredRoot)));
        if (legacy.Count > 0)
        {
            var fallback = ResolveFromSources(reference, segments, legacy, invalidRoots);
            if (fallback is not null)
                return fallback;
        }

        var suffix = invalidRoots.Count == 0 ? string.Empty
            : $" Invalid or missing roots: {string.Join(", ", invalidRoots)}.";
        return Failure(reference, AssetResolutionStatus.Missing,
            $"{UnresolvedStockDiagnostic(reference, clientDiagnostic)}{suffix}");
    }

    private ResolvedTextureAsset? ResolveFromSources(string reference, string[] segments,
        IReadOnlyList<(string Root, AssetSourceKind Kind)> sources, List<string> invalidRoots)
    {
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
            return Decode(reference, located.Path, source.Kind, source.Root);
        }
        return null;
    }

    private AssetMaterializationResult? MaterializeFromClient(string reference)
    {
        try
        {
            return _stockProvider!.Materialize(reference, _stockClient);
        }
        catch (Exception ex)
        {
            return new AssetMaterializationResult(false, reference, null, null, false,
                $"Could not read the WoW client archives: {ex.Message}");
        }
    }

    private string UnresolvedStockDiagnostic(string reference, string? materializedMessage)
    {
        if (_stockProvider is null)
            return $"No matching asset was found for \"{reference}\".";
        if (!_stockClient.IsValid)
        {
            return _stockClient.Status == WowClientValidationStatus.NotConfigured
                ? $"\"{reference}\" is not in the managed stock cache and no WoW client is configured. Configure a WoW 3.3.5a build-12340 client (or place the file in {_stockProvider.CacheRoot})."
                : $"\"{reference}\" is not in the managed stock cache and the configured WoW client is {_stockClient.Status}: {_stockClient.Message}";
        }
        return $"\"{reference}\" is not cached and the configured WoW client does not provide it. {(materializedMessage is { Length: > 0 } ? materializedMessage : string.Empty)}"
            .TrimEnd();
    }

    private static bool IsStockWoWReference(string[] segments) =>
        segments.Length > 0
        && (segments[0].Equals("Interface", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("Fonts", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Normalizes a portable project-owned design asset without applying WoW semantics.</summary>
    public static bool TryNormalizeProjectAsset(string reference, out string normalized, out string? reason)
    {
        normalized = string.Empty;
        reason = null;
        var portable = reference.Trim().Replace('\\', '/');
        if (portable.Length == 0 || portable.StartsWith('/') || Path.IsPathRooted(portable) || portable.Contains(':'))
        {
            reason = "Design assets must use a project-relative path.";
            return false;
        }

        var segments = portable.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            reason = "Design asset paths may not contain traversal segments.";
            return false;
        }
        normalized = string.Join('/', segments);
        return true;
    }

    private ResolvedTextureAsset ResolveDesignAsset(string reference)
    {
        if (!TryNormalizeProjectAsset(reference, out var normalized, out var reason))
            return Failure(reference, AssetResolutionStatus.InvalidPath, reason!);
        if (_projectRoot is null)
            return Failure(reference, AssetResolutionStatus.Missing,
                "Save the FrameForge project before resolving project-owned artwork.", AssetSourceKind.ProjectRelative);
        if (!Directory.Exists(_projectRoot))
            return Failure(reference, AssetResolutionStatus.Missing,
                $"The project directory does not exist: {_projectRoot}", AssetSourceKind.ProjectRelative, _projectRoot);

        LocateResult located;
        try
        {
            located = Locate(_projectRoot, normalized.Split('/'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure(reference, AssetResolutionStatus.Missing, ex.Message,
                AssetSourceKind.ProjectRelative, _projectRoot);
        }
        if (located.Ambiguous.Count > 0)
            return Failure(reference, AssetResolutionStatus.Ambiguous,
                $"Multiple case/extension candidates exist beneath {_projectRoot}: {string.Join(", ", located.Ambiguous.Select(Path.GetFileName))}",
                AssetSourceKind.ProjectRelative, _projectRoot);
        if (located.Path is null)
            return Failure(reference, AssetResolutionStatus.Missing,
                $"Project asset \"{reference}\" was not found beneath {_projectRoot}.",
                AssetSourceKind.ProjectRelative, _projectRoot);

        return Decode(reference, located.Path, AssetSourceKind.ProjectRelative, _projectRoot);
    }

    private ResolvedTextureAsset Decode(string reference, string path, AssetSourceKind sourceKind, string sourceRoot)
    {
        var detectedFormat = TextureFileFormat.Unknown;
        try
        {
            var info = new FileInfo(path);
            var identity = new FileIdentity(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
            if (!_decodeCache.TryGetValue(identity, out var texture))
            {
                using var stream = File.OpenRead(path);
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

            return new ResolvedTextureAsset(reference, AssetResolutionStatus.Resolved, path,
                sourceKind, sourceRoot, detectedFormat, texture,
                new AssetResolutionDiagnostic(AssetResolutionStatus.Resolved, "Resolved and decoded."));
        }
        catch (UnsupportedTextureEncodingException ex)
        {
            return Failure(reference, AssetResolutionStatus.UnsupportedFormat, ex.Message,
                sourceKind, sourceRoot, path, detectedFormat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or NotSupportedException or OverflowException)
        {
            return Failure(reference, AssetResolutionStatus.DecodeFailed, ex.Message,
                sourceKind, sourceRoot, path, detectedFormat);
        }
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
