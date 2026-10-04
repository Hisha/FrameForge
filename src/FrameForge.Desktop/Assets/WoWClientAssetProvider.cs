using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nmpq;
using War3Net.IO.Mpq;

namespace FrameForge.Desktop.Assets;

public enum WowClientValidationStatus
{
    NotConfigured,
    Invalid,
    UnsupportedBuild,
    AmbiguousLocale,
    Valid,
}

public sealed record WowClientBuild(int Major, int Minor, int Patch, int Build)
{
    public override string ToString() => $"{Major}.{Minor}.{Patch}a / {Build}";
}

public sealed record WowArchive(string PhysicalPath, string RelativePath, int Priority);

public sealed record WowClientValidation(
    WowClientValidationStatus Status,
    string? ClientPath,
    WowClientBuild? Build,
    string? Locale,
    IReadOnlyList<WowArchive> Archives,
    string Message)
{
    public bool IsValid => Status == WowClientValidationStatus.Valid;
}

public sealed record StockAssetProvenance(
    string RequestedPath,
    string ArchivePath,
    string ClientPath,
    string Build,
    string Locale,
    long Size,
    string Sha256,
    DateTimeOffset MaterializedUtc,
    string CachePath);

public sealed record AssetMaterializationResult(
    bool Success,
    string RequestedPath,
    string? CachePath,
    StockAssetProvenance? Provenance,
    bool CacheHit,
    string Message);

public interface IWoWClientBuildReader
{
    bool TryRead(string executablePath, out WowClientBuild? build, out string diagnostic);
}

public interface IWoWArchiveReader
{
    bool TryRead(string archivePath, string entryPath, out byte[]? bytes);
}

public interface IWoWClientAssetProvider
{
    string CacheRoot { get; }
    WowClientValidation ValidateClient(string? clientPath);
    AssetMaterializationResult Materialize(string reference, WowClientValidation client);
    StockAssetProvenance? GetProvenance(string physicalPath);
    void ClearCache();
}

/// <summary>Reads the PE string resource without relying on Windows APIs.</summary>
public sealed class PortableWowBuildReader : IWoWClientBuildReader
{
    private static readonly byte[] FileVersionKey = Encoding.Unicode.GetBytes("FileVersion");
    private static readonly Regex VersionPattern = new(
        @"^(\d+)\D+(\d+)\D+(\d+)\D+(\d+)$", RegexOptions.CultureInvariant);

    public bool TryRead(string executablePath, out WowClientBuild? build, out string diagnostic)
    {
        build = null;
        diagnostic = "Wow.exe has no readable FileVersion resource.";
        try
        {
            var bytes = File.ReadAllBytes(executablePath);
            var offset = bytes.AsSpan().IndexOf(FileVersionKey);
            if (offset < 0)
                return false;
            offset += FileVersionKey.Length;
            while (offset + 1 < bytes.Length && bytes[offset] == 0 && bytes[offset + 1] == 0)
                offset += 2;
            var end = offset;
            while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0))
                end += 2;
            var text = Encoding.Unicode.GetString(bytes, offset, end - offset).Trim();
            var match = VersionPattern.Match(text);
            if (!match.Success)
            {
                diagnostic = $"Wow.exe FileVersion '{text}' is not recognized.";
                return false;
            }
            build = new WowClientBuild(
                int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value));
            diagnostic = $"Detected WoW {build}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"Could not read Wow.exe: {ex.Message}";
            return false;
        }
    }
}

/// <summary>Small replaceable adapter over the managed, read-only Nmpq reader.</summary>
public sealed class ManagedMpqArchiveReader : IWoWArchiveReader
{
    public bool TryRead(string archivePath, string entryPath, out byte[]? bytes)
    {
        try
        {
            using var archive = Nmpq.MpqArchive.Open(archivePath);
            bytes = archive.ReadFile(entryPath);
            return bytes is { Length: > 0 };
        }
        catch (Nmpq.MpqParsingException)
        {
            // Nmpq covers the official WotLK v1 archives; War3Net covers classic v0 archives
            // such as locally installed mod patches. Both paths are managed and read-only.
            using var archive = War3Net.IO.Mpq.MpqArchive.Open(archivePath, loadListFile: false);
            if (!archive.FileExists(entryPath))
            {
                bytes = null;
                return false;
            }
            using var source = archive.OpenFile(entryPath);
            if (!source.CanRead)
                throw new InvalidDataException($"The MPQ entry '{entryPath}' cannot be decoded by the managed reader.");
            using var destination = new MemoryStream();
            source.CopyTo(destination);
            bytes = destination.ToArray();
            return bytes.Length > 0;
        }
    }
}

/// <summary>Validates a 3.3.5a client and materializes only requested Interface assets.</summary>
public sealed class WoWClientAssetProvider : IWoWClientAssetProvider
{
    public const int SupportedBuild = 12340;
    private const string BuildCacheName = "wow-3.3.5a-12340";
    private const string ProvenanceFileName = ".frameforge-provenance.json";
    private static readonly string[] Extensions = ["", ".tga", ".blp", ".png"];
    private static readonly Regex LocalePattern = new("^[a-z]{2}[A-Z]{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex GlobalPatchPattern = new("^patch(?:-(\\d+))?\\.mpq$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly IWoWArchiveReader _archives;
    private readonly IWoWClientBuildReader _builds;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public WoWClientAssetProvider(string? cacheRoot = null, IWoWArchiveReader? archives = null,
        IWoWClientBuildReader? builds = null)
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        CacheRoot = Path.GetFullPath(cacheRoot
            ?? Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CACHE_ROOT")
            ?? Path.Combine(applicationData, "FrameForge", "assets", BuildCacheName));
        _archives = archives ?? new ManagedMpqArchiveReader();
        _builds = builds ?? new PortableWowBuildReader();
    }

    public string CacheRoot { get; }

    public WowClientValidation ValidateClient(string? clientPath)
    {
        if (string.IsNullOrWhiteSpace(clientPath))
            return Invalid(WowClientValidationStatus.NotConfigured, null, "No WoW client is configured.");
        string root;
        try { root = Path.GetFullPath(clientPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { return Invalid(WowClientValidationStatus.Invalid, clientPath, $"Invalid client path: {ex.Message}"); }
        var executable = Path.Combine(root, "Wow.exe");
        var data = Path.Combine(root, "Data");
        if (!File.Exists(executable) || !Directory.Exists(data))
            return Invalid(WowClientValidationStatus.Invalid, root, "Expected Wow.exe and Data were not found.");
        if (!_builds.TryRead(executable, out var build, out var buildDiagnostic) || build is null)
            return Invalid(WowClientValidationStatus.Invalid, root, buildDiagnostic);
        if (build != new WowClientBuild(3, 3, 5, SupportedBuild))
            return new WowClientValidation(WowClientValidationStatus.UnsupportedBuild, root, build, null, [],
                $"Unsupported WoW build {build}; FrameForge supports only 3.3.5a / {SupportedBuild}.");

        var locales = Directory.EnumerateDirectories(data)
            .Select(Path.GetFileName)
            .Where(name => name is not null && LocalePattern.IsMatch(name))
            .Where(name => File.Exists(FindCaseInsensitive(Path.Combine(data, name!), $"locale-{name}.MPQ")))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (locales.Length == 0)
            return new WowClientValidation(WowClientValidationStatus.Invalid, root, build, null, [],
                "No locale directory with a matching locale archive was found.");
        if (locales.Length > 1)
            return new WowClientValidation(WowClientValidationStatus.AmbiguousLocale, root, build, null, [],
                $"Multiple client locales were found: {string.Join(", ", locales)}.");

        var discovered = DiscoverArchives(root, locales[0]);
        if (discovered.Count == 0)
            return new WowClientValidation(WowClientValidationStatus.Invalid, root, build, locales[0], [],
                "No supported 3.3.5a data archives were found.");
        return new WowClientValidation(WowClientValidationStatus.Valid, root, build, locales[0], discovered,
            $"Valid WoW {build} client ({locales[0]}, {discovered.Count} data archives)." );
    }

    public AssetMaterializationResult Materialize(string reference, WowClientValidation client)
    {
        if (!client.IsValid || client.ClientPath is null || client.Build is null || client.Locale is null)
            return Failure(reference, "A validated WoW 3.3.5a client is required.");
        if (!TryNormalizeClientResourcePath(reference, out var segments, out var error))
            return Failure(reference, error);

        foreach (var candidate in CandidatePaths(segments))
        {
            var destination = SafeDestination(candidate);
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
            {
                var cached = GetProvenance(destination) ?? CreateCacheProvenance(reference, destination, client);
                return new AssetMaterializationResult(true, reference, destination, cached, true, "Already available in the managed cache.");
            }
        }

        foreach (var archive in client.Archives.OrderByDescending(item => item.Priority))
        foreach (var candidate in CandidatePaths(segments))
        {
            var archivePath = string.Join('\\', candidate);
            byte[]? bytes;
            try
            {
                if (!_archives.TryRead(archive.PhysicalPath, archivePath, out bytes) || bytes is not { Length: > 0 })
                    continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or NotSupportedException or MpqParsingException or War3Net.IO.Mpq.MpqParserException)
            {
                return Failure(reference, $"Could not read {archive.RelativePath}: {ex.Message}");
            }

            var destination = SafeDestination(candidate);
            EnsureNoSymlinkEscape(Path.GetDirectoryName(destination)!, create: true);
            WriteAtomic(destination, bytes);
            var provenance = new StockAssetProvenance(reference, archive.RelativePath, client.ClientPath,
                client.Build.ToString(), client.Locale, bytes.LongLength, Sha256(bytes), DateTimeOffset.UtcNow, destination);
            SaveProvenance(provenance);
            return new AssetMaterializationResult(true, reference, destination, provenance, false,
                $"Materialized from {archive.RelativePath}.");
        }
        return Failure(reference, "No effective client archive contains this Interface asset.");
    }

    public StockAssetProvenance? GetProvenance(string physicalPath)
    {
        var fullPath = Path.GetFullPath(physicalPath);
        if (!IsWithin(CacheRoot, fullPath) || !File.Exists(ProvenancePath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ProvenanceManifest>(File.ReadAllText(ProvenancePath), _json)?.Assets
                .FirstOrDefault(asset => Path.GetFullPath(asset.CachePath).Equals(fullPath, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void ClearCache()
    {
        if (!Directory.Exists(CacheRoot))
            return;
        var info = new DirectoryInfo(CacheRoot);
        if (info.LinkTarget is not null)
            throw new IOException("Refusing to clear a managed cache root that is a symbolic link.");
        Directory.Delete(CacheRoot, recursive: true);
    }

    public static IReadOnlyList<WowArchive> DiscoverArchives(string clientRoot, string locale)
    {
        var data = Path.Combine(clientRoot, "Data");
        var localeRoot = FindCaseInsensitive(data, locale);
        var ordered = new List<string>();
        AddKnown(data, ordered, "common.MPQ", "common-2.MPQ", "expansion.MPQ", "lichking.MPQ");
        AddKnown(localeRoot, ordered, $"locale-{locale}.MPQ", $"expansion-locale-{locale}.MPQ", $"lichking-locale-{locale}.MPQ");
        AddPatches(data, ordered, GlobalPatchPattern);
        AddPatches(localeRoot, ordered,
            new Regex($"^patch-{Regex.Escape(locale)}(?:-(\\d+))?\\.mpq$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        return ordered.Select((path, priority) => new WowArchive(path,
            Path.GetRelativePath(clientRoot, path).Replace(Path.DirectorySeparatorChar, '/'), priority)).ToArray();
    }

    public static bool TryNormalizeInterfacePath(string? reference, out string[] segments, out string error)
        => TryNormalizeClientResourcePath(reference, out segments, out error, interfaceOnly: true);

    public static bool TryNormalizeClientResourcePath(string? reference, out string[] segments, out string error)
        => TryNormalizeClientResourcePath(reference, out segments, out error, interfaceOnly: false);

    private static bool TryNormalizeClientResourcePath(
        string? reference,
        out string[] segments,
        out string error,
        bool interfaceOnly)
    {
        segments = [];
        error = "Texture reference is empty.";
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || reference.Contains(':'))
            return false;
        var normalized = reference.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.EndsWith('/') || normalized.Contains("//", StringComparison.Ordinal))
        {
            error = "Malformed archive separators are not accepted.";
            return false;
        }
        var parts = normalized.Split('/');
        var root = parts.Length > 0 ? parts[0] : string.Empty;
        var allowedRoot = root.Equals("Interface", StringComparison.OrdinalIgnoreCase)
                          || (!interfaceOnly && root.Equals("Fonts", StringComparison.OrdinalIgnoreCase));
        if (parts.Length < 2 || !allowedRoot
            || parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            error = interfaceOnly
                ? "Only safe archive-relative Interface paths are accepted."
                : "Only safe archive-relative Interface or Fonts paths are accepted.";
            return false;
        }
        segments = [root.Equals("Fonts", StringComparison.OrdinalIgnoreCase) ? "Fonts" : "Interface", .. parts.Skip(1)];
        error = string.Empty;
        return true;
    }

    private string ProvenancePath => Path.Combine(CacheRoot, ProvenanceFileName);

    private static IEnumerable<string[]> CandidatePaths(string[] segments)
    {
        if (Path.HasExtension(segments[^1]))
        {
            yield return segments;
            yield break;
        }
        foreach (var extension in Extensions.Skip(1))
        {
            var candidate = (string[])segments.Clone();
            candidate[^1] += extension;
            yield return candidate;
        }
    }

    private string SafeDestination(string[] segments)
    {
        var destination = Path.GetFullPath(Path.Combine([CacheRoot, .. segments]));
        if (!IsWithin(CacheRoot, destination))
            throw new InvalidDataException("The materialized path would escape the managed cache.");
        EnsureNoSymlinkEscape(Path.GetDirectoryName(destination)!, create: false);
        if (File.Exists(destination) && new FileInfo(destination).LinkTarget is not null)
            throw new IOException("Refusing to replace a symbolic link in the managed cache.");
        return destination;
    }

    private void EnsureNoSymlinkEscape(string directory, bool create)
    {
        if (create)
            Directory.CreateDirectory(CacheRoot);
        var rootInfo = new DirectoryInfo(CacheRoot);
        if (rootInfo.Exists && rootInfo.LinkTarget is not null)
            throw new IOException("The managed cache root cannot be a symbolic link.");
        var relative = Path.GetRelativePath(CacheRoot, directory);
        var current = CacheRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (info.Exists && info.LinkTarget is not null)
                throw new IOException("A symbolic link would escape the managed cache.");
            if (create)
                Directory.CreateDirectory(current);
        }
    }

    private static void WriteAtomic(string destination, byte[] bytes)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void SaveProvenance(StockAssetProvenance provenance)
    {
        var assets = File.Exists(ProvenancePath)
            ? JsonSerializer.Deserialize<ProvenanceManifest>(File.ReadAllText(ProvenancePath), _json)?.Assets ?? []
            : [];
        assets.RemoveAll(item => Path.GetFullPath(item.CachePath).Equals(Path.GetFullPath(provenance.CachePath), StringComparison.Ordinal));
        assets.Add(provenance);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ProvenanceManifest { Assets = assets }, _json));
        WriteAtomic(ProvenancePath, bytes);
    }

    private static StockAssetProvenance CreateCacheProvenance(string reference, string path, WowClientValidation client)
    {
        var bytes = File.ReadAllBytes(path);
        return new StockAssetProvenance(reference, "managed cache", client.ClientPath!, client.Build!.ToString(),
            client.Locale!, bytes.LongLength, Sha256(bytes), File.GetLastWriteTimeUtc(path), path);
    }

    private static void AddKnown(string directory, List<string> result, params string[] names)
    {
        foreach (var name in names)
        {
            var path = FindCaseInsensitive(directory, name);
            if (File.Exists(path))
                result.Add(path);
        }
    }

    private static void AddPatches(string directory, List<string> result, Regex pattern)
    {
        if (!Directory.Exists(directory))
            return;
        result.AddRange(Directory.EnumerateFiles(directory, "*.MPQ")
            .Select(path => (Path: path, Match: pattern.Match(Path.GetFileName(path))))
            .Where(item => item.Match.Success)
            .OrderBy(item => item.Match.Groups[1].Success ? int.Parse(item.Match.Groups[1].Value) : 0)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path));
    }

    private static string FindCaseInsensitive(string directory, string name)
    {
        if (!Directory.Exists(directory))
            return Path.Combine(directory, name);
        return Directory.EnumerateFileSystemEntries(directory)
                   .FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? Path.Combine(directory, name);
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static AssetMaterializationResult Failure(string reference, string message) =>
        new(false, reference, null, null, false, message);
    private static WowClientValidation Invalid(WowClientValidationStatus status, string? path, string message) =>
        new(status, path, null, null, [], message);

    private sealed class ProvenanceManifest
    {
        public List<StockAssetProvenance> Assets { get; set; } = [];
    }
}
