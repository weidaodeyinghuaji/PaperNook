using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaperTodo;

internal enum UpdateChannel
{
    Stable,
    Beta
}

internal enum ReleaseDistribution
{
    Unknown,
    SelfContained,
    NoRuntime,
    Portable
}

internal sealed record ReleaseAsset(
    ReleaseDistribution Distribution,
    string FileName,
    PaperTodoVersion Version,
    long Length,
    string Sha256,
    Uri DownloadUrl);

internal sealed class ReleaseManifest
{
    internal const long MaximumAssetLength = 300L * 1024 * 1024;
    private static readonly HashSet<string> RootProperties =
        ["schema", "repository", "tag", "version", "publishedUtc", "assets"];
    private static readonly HashSet<string> AssetProperties =
        ["distribution", "fileName", "version", "length", "sha256", "downloadUrl"];
    private static readonly Regex FileNamePattern = new(
        "^PaperNook-v[0-9A-Za-z.-]+-win-x64-(?:self-contained|no-runtime)\\.exe$|^PaperNook-v[0-9A-Za-z.-]+-win-x64-portable\\.zip$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private ReleaseManifest(
        int schema,
        string repository,
        string tag,
        PaperTodoVersion version,
        DateTimeOffset publishedUtc,
        IReadOnlyList<ReleaseAsset> assets)
    {
        Schema = schema;
        Repository = repository;
        Tag = tag;
        Version = version;
        PublishedUtc = publishedUtc;
        Assets = assets;
    }

    internal int Schema { get; }
    internal string Repository { get; }
    internal string Tag { get; }
    internal PaperTodoVersion Version { get; }
    internal DateTimeOffset PublishedUtc { get; }
    internal IReadOnlyList<ReleaseAsset> Assets { get; }

    internal static ReleaseManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > 1024 * 1024)
        {
            throw new InvalidDataException("Release manifest size is invalid.");
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var root = document.RootElement;
            RequireObjectProperties(root, RootProperties, "manifest");

            var schema = RequiredInt32(root, "schema");
            if (schema != 1) throw new InvalidDataException("Release manifest schema must be 1.");

            var repository = RequiredString(root, "repository");
            if (!string.Equals(repository, ReleaseTrust.Repository, StringComparison.Ordinal))
                throw new InvalidDataException("Release manifest repository is not trusted.");

            var version = PaperTodoVersion.Parse(RequiredString(root, "version"));
            var tag = RequiredString(root, "tag");
            if (!string.Equals(tag, "v" + version, StringComparison.Ordinal))
                throw new InvalidDataException("Release tag does not match manifest version.");

            var publishedText = RequiredString(root, "publishedUtc");
            if (!DateTimeOffset.TryParse(
                    publishedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var publishedUtc) || publishedUtc.Offset != TimeSpan.Zero || !publishedText.EndsWith('Z'))
                throw new InvalidDataException("publishedUtc must be an ISO-8601 UTC value.");

            var assetsElement = root.GetProperty("assets");
            if (assetsElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Release manifest assets must be an array.");

            var assets = new List<ReleaseAsset>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in assetsElement.EnumerateArray())
            {
                RequireObjectProperties(element, AssetProperties, "asset");
                var distribution = ParseDistribution(RequiredString(element, "distribution"));
                var fileName = RequiredString(element, "fileName");
                ValidateFileName(fileName);
                if (!names.Add(fileName)) throw new InvalidDataException("Release asset fileName is duplicated.");

                var assetVersion = PaperTodoVersion.Parse(RequiredString(element, "version"));
                if (assetVersion != version) throw new InvalidDataException("Release asset version does not match manifest version.");

                var length = RequiredInt64(element, "length");
                if (length <= 0 || length > MaximumAssetLength)
                    throw new InvalidDataException("Release asset length is outside the allowed range.");

                var sha256 = RequiredString(element, "sha256");
                if (sha256.Length != 64 || sha256.Any(static value => !IsLowerHex(value)))
                    throw new InvalidDataException("Release asset SHA-256 is invalid.");

                var urlText = RequiredString(element, "downloadUrl");
                if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url))
                    throw new InvalidDataException("Release asset download URL is invalid.");
                ValidateDownloadUrl(url, tag, fileName);
                ValidateDistributionFileName(distribution, fileName);
                assets.Add(new ReleaseAsset(distribution, fileName, assetVersion, length, sha256, url));
            }

            if (assets.Count == 0) throw new InvalidDataException("Release manifest has no assets.");
            return new ReleaseManifest(schema, repository, tag, version, publishedUtc, assets);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Release manifest is invalid.", ex);
        }
    }

    internal ReleaseAsset? SelectUpdate(
        PaperTodoVersion currentVersion,
        UpdateChannel channel,
        ReleaseDistribution currentDistribution)
    {
        if (Version <= currentVersion) return null;
        if (channel == UpdateChannel.Stable && Version.IsPrerelease) return null;
        if (channel == UpdateChannel.Beta && Version.PrereleaseKind is not (null or "alpha" or "beta" or "rc")) return null;

        var exact = Assets.FirstOrDefault(asset => asset.Distribution == currentDistribution);
        return exact ?? Assets.FirstOrDefault(static asset => asset.Distribution == ReleaseDistribution.SelfContained);
    }

    private static void RequireObjectProperties(JsonElement element, HashSet<string> allowed, string label)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Release {label} must be an object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!found.Add(property.Name)) throw new InvalidDataException($"Duplicate release {label} property: {property.Name}");
            if (!allowed.Contains(property.Name)) throw new InvalidDataException($"Unknown release {label} property: {property.Name}");
        }
        foreach (var name in allowed)
        {
            if (!found.Contains(name)) throw new InvalidDataException($"Missing release {label} property: {name}");
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var property = element.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Release property {name} must be a string.");
        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"Release property {name} is empty.");
        return value;
    }

    private static int RequiredInt32(JsonElement element, string name)
    {
        var property = element.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
            throw new InvalidDataException($"Release property {name} must be an integer.");
        return value;
    }

    private static long RequiredInt64(JsonElement element, string name)
    {
        var property = element.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
            throw new InvalidDataException($"Release property {name} must be an integer.");
        return value;
    }

    private static ReleaseDistribution ParseDistribution(string value) => value switch
    {
        "self-contained" => ReleaseDistribution.SelfContained,
        "no-runtime" => ReleaseDistribution.NoRuntime,
        "portable" => ReleaseDistribution.Portable,
        _ => throw new InvalidDataException("Release asset distribution is invalid.")
    };

    private static void ValidateFileName(string fileName)
    {
        if (fileName.Length > 180 || fileName != Path.GetFileName(fileName) || !FileNamePattern.IsMatch(fileName))
            throw new InvalidDataException("Release asset fileName is invalid.");
    }

    private static void ValidateDistributionFileName(ReleaseDistribution distribution, string fileName)
    {
        var suffix = distribution switch
        {
            ReleaseDistribution.SelfContained => "-self-contained.exe",
            ReleaseDistribution.NoRuntime => "-no-runtime.exe",
            ReleaseDistribution.Portable => "-portable.zip",
            _ => ""
        };
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release asset distribution does not match fileName.");
    }

    private static void ValidateDownloadUrl(Uri url, string tag, string fileName)
    {
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !url.IsDefaultPort || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidDataException("Release asset download URL is not trusted.");

        var expectedPath = $"/{ReleaseTrust.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}";
        if (!string.Equals(url.AbsolutePath, expectedPath, StringComparison.Ordinal))
            throw new InvalidDataException("Release asset download URL does not match repository, tag, or fileName.");
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

internal readonly record struct PaperTodoVersion(
    int Major,
    int Minor,
    int Patch,
    string? PrereleaseKind,
    int? PrereleaseNumber) : IComparable<PaperTodoVersion>
{
    private static readonly Regex Pattern = new(
        "^(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:-(?<kind>alpha|beta|rc)(?:\\.(?<number>0|[1-9][0-9]*))?)?$",
        RegexOptions.CultureInvariant);

    internal bool IsPrerelease => PrereleaseKind != null;

    internal static PaperTodoVersion Current
    {
        get
        {
            var value = typeof(PaperTodoVersion).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "0.0.0";
            var separator = value.IndexOf('+');
            if (separator >= 0) value = value[..separator];
            return Parse(value);
        }
    }

    internal static PaperTodoVersion Parse(string value)
    {
        var match = Pattern.Match(value ?? "");
        if (!match.Success) throw new InvalidDataException("PaperNook version is not supported.");
        return new PaperTodoVersion(
            int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture),
            match.Groups["kind"].Success ? match.Groups["kind"].Value : null,
            match.Groups["number"].Success ? int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture) : null);
    }

    public int CompareTo(PaperTodoVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (PrereleaseKind == null) return other.PrereleaseKind == null ? 0 : 1;
        if (other.PrereleaseKind == null) return -1;
        result = PrereleaseRank(PrereleaseKind).CompareTo(PrereleaseRank(other.PrereleaseKind));
        if (result != 0) return result;
        return (PrereleaseNumber ?? 0).CompareTo(other.PrereleaseNumber ?? 0);
    }

    public override string ToString() =>
        PrereleaseKind == null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{PrereleaseKind}" + (PrereleaseNumber.HasValue ? $".{PrereleaseNumber.Value}" : "");

    public static bool operator <(PaperTodoVersion left, PaperTodoVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(PaperTodoVersion left, PaperTodoVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(PaperTodoVersion left, PaperTodoVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(PaperTodoVersion left, PaperTodoVersion right) => left.CompareTo(right) >= 0;

    private static int PrereleaseRank(string value) => value switch
    {
        "alpha" => 0,
        "beta" => 1,
        "rc" => 2,
        _ => -1
    };
}
