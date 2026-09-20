using System.Reflection;
using System.Security.Cryptography;

namespace PaperTodo;

internal sealed class ReleaseTrust
{
    internal const string RepositoryOwner = ProductIdentity.RepositoryOwner;
    internal const string RepositoryName = ProductIdentity.RepositoryName;
    internal const string Repository = RepositoryOwner + "/" + RepositoryName;

    internal ReleaseTrust(string publisherSubject, IEnumerable<string> allowedPublisherSpkiSha256)
    {
        PublisherSubject = publisherSubject?.Trim() ?? "";
        AllowedPublisherSpkiSha256 = allowedPublisherSpkiSha256
            .Select(static value => value.Trim().ToLowerInvariant())
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal string PublisherSubject { get; }
    internal IReadOnlyList<string> AllowedPublisherSpkiSha256 { get; }
    internal bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublisherSubject) &&
        AllowedPublisherSpkiSha256.Count > 0 &&
        AllowedPublisherSpkiSha256.All(static value =>
            value.Length == 64 && value.All(Uri.IsHexDigit));

    internal static ReleaseTrust Current { get; } = FromAssembly(typeof(ReleaseTrust).Assembly);

    internal static ReleaseDistribution CurrentDistribution =>
        ParseDistribution(ReadMetadata(typeof(ReleaseTrust).Assembly, "PaperNookDistribution"));

    internal static string ComputeSpkiSha256(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    private static ReleaseTrust FromAssembly(Assembly assembly)
    {
        var subject = ReadMetadata(assembly, "PaperNookPublisherSubject");
        var pins = ReadMetadata(assembly, "PaperNookPublisherSpkiSha256")
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ReleaseTrust(subject, pins);
    }

    private static string ReadMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? "";

    private static ReleaseDistribution ParseDistribution(string value) => value switch
    {
        "self-contained" => ReleaseDistribution.SelfContained,
        "no-runtime" => ReleaseDistribution.NoRuntime,
        "portable" => ReleaseDistribution.Portable,
        _ => ReleaseDistribution.Unknown
    };
}
