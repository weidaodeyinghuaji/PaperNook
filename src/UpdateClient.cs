using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace PaperTodo;

internal enum UpdateCheckStatus
{
    NoUpdate,
    Available,
    Disabled,
    Failed
}

internal sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    ReleaseAsset? Asset,
    string Message,
    string? ETag = null);

internal sealed record UpdateProgress(long BytesReceived, long TotalBytes);

internal sealed record VerifiedUpdate(ReleaseAsset Asset, string DownloadPath, AuthenticodeVerification Signature);

internal sealed class UpdateClient : IDisposable
{
    private static readonly Uri StableManifestUri = new(
        "https://github.com/weidaodeyinghuaji/PaperNook/releases/latest/download/release-manifest.json");
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly ReleaseTrust _trust;
    private readonly IAuthenticodeVerifier _authenticodeVerifier;
    private readonly PaperTodoVersion _currentVersion;
    private readonly ReleaseDistribution _currentDistribution;
    private readonly bool _ownsHttpClient;

    internal UpdateClient(
        HttpClient httpClient,
        string cacheDirectory,
        ReleaseTrust trust,
        IAuthenticodeVerifier authenticodeVerifier,
        PaperTodoVersion? currentVersion = null,
        ReleaseDistribution? currentDistribution = null,
        bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cacheDirectory = Path.GetFullPath(cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory)));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _authenticodeVerifier = authenticodeVerifier ?? throw new ArgumentNullException(nameof(authenticodeVerifier));
        _currentVersion = currentVersion ?? PaperTodoVersion.Current;
        _currentDistribution = currentDistribution ?? ReleaseTrust.CurrentDistribution;
        _ownsHttpClient = ownsHttpClient;
    }

    internal static UpdateClient CreateDefault(string cacheDirectory)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PaperNook-UpdateClient/1.0");
        return new UpdateClient(client, cacheDirectory, ReleaseTrust.Current, new AuthenticodeVerifier(), ownsHttpClient: true);
    }

    internal async Task<UpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken)
    {
        if (!_trust.IsConfigured)
            return new UpdateCheckResult(UpdateCheckStatus.Disabled, null, "Release trust is not configured.");

        try
        {
            var manifestUri = channel == UpdateChannel.Stable
                ? StableManifestUri
                : await ResolvePrereleaseManifestUriAsync(cancellationToken).ConfigureAwait(false);
            if (manifestUri == null)
                return new UpdateCheckResult(UpdateCheckStatus.NoUpdate, null, "No beta release was found.");

            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadBoundedAsync(response.Content, 1024 * 1024, timeout.Token).ConfigureAwait(false);
            var manifest = ReleaseManifest.Parse(bytes);
            var selected = manifest.SelectUpdate(_currentVersion, channel, _currentDistribution);
            return selected == null
                ? new UpdateCheckResult(UpdateCheckStatus.NoUpdate, null, "PaperNook is up to date.", response.Headers.ETag?.Tag)
                : new UpdateCheckResult(UpdateCheckStatus.Available, selected, "An update is available.", response.Headers.ETag?.Tag);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "Update check timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, ex.Message);
        }
    }

    internal async Task<VerifiedUpdate> DownloadAndVerifyAsync(
        ReleaseAsset asset,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!_trust.IsConfigured) throw new InvalidOperationException("Release trust is not configured.");
        if (asset.Length <= 0 || asset.Length > ReleaseManifest.MaximumAssetLength)
            throw new InvalidDataException("Release asset length is outside the allowed range.");

        var versionDirectory = Path.Combine(_cacheDirectory, "Updates", asset.Version.ToString());
        Directory.CreateDirectory(versionDirectory);
        var destination = Path.Combine(versionDirectory, "PaperNook.download");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var response = await _httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared && declared != asset.Length)
                throw new InvalidDataException("Downloaded asset length does not match the manifest.");

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var target = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long received = 0;
            try
            {
                while (true)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > asset.Length || received > ReleaseManifest.MaximumAssetLength)
                        throw new InvalidDataException("Downloaded asset exceeded the manifest length.");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress?.Report(new UpdateProgress(received, asset.Length));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            await target.FlushAsync(timeout.Token).ConfigureAwait(false);
            target.Flush(flushToDisk: true);

            if (received != asset.Length) throw new InvalidDataException("Downloaded asset is truncated.");
            var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Downloaded asset SHA-256 does not match the manifest.");

            var signature = _authenticodeVerifier.Verify(destination, _trust);
            if (!signature.IsTrusted) throw new InvalidDataException($"Downloaded asset signature is not trusted: {signature.Failure}");
            if (PaperTodoVersion.Parse(signature.ProductVersion) != asset.Version)
                throw new InvalidDataException("Downloaded executable product version does not match the manifest.");

            return new VerifiedUpdate(asset, destination, signature);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private async Task<Uri?> ResolvePrereleaseManifestUriAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/repos/weidaodeyinghuaji/PaperNook/releases?per_page=10");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response.Content, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), "release-manifest.json", StringComparison.Ordinal)) continue;
                var url = asset.GetProperty("browser_download_url").GetString();
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                    return uri;
            }
        }
        return null;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes)
            throw new InvalidDataException("Response is too large.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximumBytes) throw new InvalidDataException("Response is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
