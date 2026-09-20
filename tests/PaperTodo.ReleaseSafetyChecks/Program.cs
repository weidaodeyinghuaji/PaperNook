using System.Text;
using System.Net;
using System.Net.Http;
using PaperTodo;

const string ValidFileName = "PaperNook-v1.1.0-win-x64-self-contained.exe";
const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
const string ValidUrl = "https://github.com/weidaodeyinghuaji/PaperNook/releases/download/v1.1.0/PaperNook-v1.1.0-win-x64-self-contained.exe";

var checks = new (string Name, Action Run)[]
{
    ("manifest-schema-one-parses", ManifestSchemaOneParses),
    ("manifest-rejects-unknown-root-property", () => Rejects(ValidManifest().Replace("\"assets\":", "\"unexpected\":true,\"assets\":"), "unknown root")),
    ("manifest-rejects-unknown-asset-property", () => Rejects(ValidManifest().Replace("\"downloadUrl\":", "\"unexpected\":true,\"downloadUrl\":"), "unknown asset")),
    ("manifest-rejects-duplicate-property", () => Rejects(ValidManifest().Replace("\"schema\": 1", "\"schema\": 1,\"schema\": 1"), "duplicate")),
    ("manifest-rejects-empty-url", () => Rejects(ValidManifest().Replace(ValidUrl, ""), "empty URL")),
    ("manifest-rejects-http-url", () => Rejects(ValidManifest().Replace("https://", "http://"), "HTTP URL")),
    ("manifest-rejects-foreign-repository", () => Rejects(ValidManifest().Replace("weidaodeyinghuaji/PaperNook", "attacker/PaperNook"), "foreign repository")),
    ("manifest-rejects-path-file-name", () => Rejects(ValidManifest().Replace(ValidFileName, "folder/PaperNook.exe"), "path file name")),
    ("manifest-rejects-invalid-sha", () => Rejects(ValidManifest().Replace(ValidSha, "1234"), "invalid SHA")),
    ("manifest-rejects-oversized-asset", () => Rejects(ValidManifest().Replace("\"length\": 4", "\"length\": 314572801"), "oversized asset")),
    ("stable-channel-rejects-prerelease", StableChannelRejectsPrerelease),
    ("beta-channel-accepts-rc", BetaChannelAcceptsRc),
    ("selection-never-downgrades", SelectionNeverDowngrades),
    ("selection-falls-back-to-self-contained", SelectionFallsBackToSelfContained),
    ("release-trust-without-pins-is-disabled", ReleaseTrustWithoutPinsIsDisabled),
    ("update-state-nonce-is-single-use", UpdateStateNonceIsSingleUse),
    ("update-state-rejects-wrong-target", UpdateStateRejectsWrongTarget),
    ("apply-target-rejects-root-and-unc", ApplyTargetRejectsRootAndUnc),
    ("update-health-requires-matching-nonce-target-version", UpdateHealthRequiresMatchingNonceTargetVersion),
    ("installer-prepares-executable-coordinator", InstallerPreparesExecutableCoordinator)
};

var failed = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {check.Name}: {ex.Message}");
    }
}

var asyncChecks = new (string Name, Func<Task> Run)[]
{
    ("verified-download-is-staged", VerifiedDownloadIsStaged),
    ("hash-mismatch-removes-download", HashMismatchRemovesDownload),
    ("oversized-content-length-is-rejected", OversizedContentLengthIsRejected),
    ("apply-recovers-waiting-parent-interruption", () => InterruptedApplyRecovers(UpdateApplyStage.WaitingParent)),
    ("apply-recovers-previous-copied-interruption", () => InterruptedApplyRecovers(UpdateApplyStage.PreviousCopied)),
    ("apply-recovers-target-temp-written-interruption", () => InterruptedApplyRecovers(UpdateApplyStage.TargetTempWritten)),
    ("apply-recovers-target-replaced-interruption", () => InterruptedApplyRecovers(UpdateApplyStage.TargetReplaced)),
    ("apply-recovers-new-launched-interruption", () => InterruptedApplyRecovers(UpdateApplyStage.NewLaunched)),
    ("apply-recovers-health-timeout-interruption", () => InterruptedApplyRecovers(UpdateApplyStage.HealthTimeout))
};
foreach (var check in asyncChecks)
{
    try
    {
        await check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {check.Name}: {ex.Message}");
    }
}

if (failed != 0)
{
    Console.Error.WriteLine($"Release safety checks failed: {failed}/{checks.Length}");
    return 1;
}

Console.WriteLine($"Release safety checks passed: {checks.Length + asyncChecks.Length}/{checks.Length + asyncChecks.Length}");
return 0;

static string ValidManifest(
    string version = "1.1.0",
    string distribution = "self-contained",
    string? fileName = null,
    string? url = null)
{
    fileName ??= $"PaperNook-v{version}-win-x64-self-contained.exe";
    url ??= $"https://github.com/weidaodeyinghuaji/PaperNook/releases/download/v{version}/{fileName}";
    return $$"""
    {
      "schema": 1,
      "repository": "weidaodeyinghuaji/PaperNook",
      "tag": "v{{version}}",
      "version": "{{version}}",
      "publishedUtc": "2026-09-20T00:00:00Z",
      "assets": [
        {
          "distribution": "{{distribution}}",
          "fileName": "{{fileName}}",
          "version": "{{version}}",
          "length": 4,
          "sha256": "{{ValidSha}}",
          "downloadUrl": "{{url}}"
        }
      ]
    }
    """;
}

static void ManifestSchemaOneParses()
{
    var manifest = ReleaseManifest.Parse(Encoding.UTF8.GetBytes(ValidManifest()));
    Assert(manifest.Schema == 1, "schema changed");
    Assert(manifest.Assets.Count == 1, "asset count changed");
    Assert(manifest.Assets[0].Distribution == ReleaseDistribution.SelfContained, "distribution was not parsed");
}

static void Rejects(string json, string scenario)
{
    try
    {
        _ = ReleaseManifest.Parse(Encoding.UTF8.GetBytes(json));
        throw new InvalidOperationException($"Manifest accepted {scenario}.");
    }
    catch (InvalidDataException)
    {
    }
}

static void StableChannelRejectsPrerelease()
{
    var manifest = ReleaseManifest.Parse(Encoding.UTF8.GetBytes(ValidManifest("1.1.0-beta.1")));
    Assert(manifest.SelectUpdate(PaperTodoVersion.Parse("1.0.0"), UpdateChannel.Stable, ReleaseDistribution.SelfContained) == null,
        "stable channel accepted a prerelease");
}

static void BetaChannelAcceptsRc()
{
    var manifest = ReleaseManifest.Parse(Encoding.UTF8.GetBytes(ValidManifest("1.1.0-rc.1")));
    var selected = manifest.SelectUpdate(PaperTodoVersion.Parse("1.0.0"), UpdateChannel.Beta, ReleaseDistribution.SelfContained);
    Assert(selected?.Version.ToString() == "1.1.0-rc.1", "beta channel rejected rc");
}

static void SelectionNeverDowngrades()
{
    var manifest = ReleaseManifest.Parse(Encoding.UTF8.GetBytes(ValidManifest("1.1.0")));
    Assert(manifest.SelectUpdate(PaperTodoVersion.Parse("1.2.0"), UpdateChannel.Beta, ReleaseDistribution.SelfContained) == null,
        "selection offered a downgrade");
}

static void SelectionFallsBackToSelfContained()
{
    var manifest = ReleaseManifest.Parse(Encoding.UTF8.GetBytes(ValidManifest("1.1.0")));
    var selected = manifest.SelectUpdate(PaperTodoVersion.Parse("1.0.0"), UpdateChannel.Stable, ReleaseDistribution.NoRuntime);
    Assert(selected?.Distribution == ReleaseDistribution.SelfContained, "selection did not use self-contained fallback");
}

static void ReleaseTrustWithoutPinsIsDisabled()
{
    var trust = new ReleaseTrust("CN=PaperNook Test", Array.Empty<string>());
    Assert(!trust.IsConfigured, "empty SPKI allowlist enabled updates");
}

static void UpdateStateNonceIsSingleUse()
{
    using var scope = new TempDirectory();
    var store = new UpdateStateStore(scope.Path, DurableAtomicFileWriter.Shared);
    var source = System.IO.Path.Combine(scope.Path, "PaperNook.download");
    var target = System.IO.Path.Combine(scope.Path, "PaperNook.exe");
    File.WriteAllBytes(source, [1, 2, 3, 4]);
    store.RegisterPendingApply(NewAsset("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", 4), source, target, "nonce-123");

    Assert(store.TryAuthorizeApply("nonce-123", target, out var pending), "registered nonce was rejected");
    Assert(pending.SourcePath == source, "authorized source path changed");
    Assert(!store.TryAuthorizeApply("nonce-123", target, out _), "nonce was accepted twice");
}

static void UpdateStateRejectsWrongTarget()
{
    using var scope = new TempDirectory();
    var store = new UpdateStateStore(scope.Path, DurableAtomicFileWriter.Shared);
    var source = System.IO.Path.Combine(scope.Path, "PaperNook.download");
    var target = System.IO.Path.Combine(scope.Path, "PaperNook.exe");
    File.WriteAllBytes(source, [1, 2, 3, 4]);
    store.RegisterPendingApply(NewAsset("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", 4), source, target, "nonce-123");

    Assert(!store.TryAuthorizeApply("nonce-123", System.IO.Path.Combine(scope.Path, "Other.exe"), out _),
        "pending apply authorized a different target");
}

static void ApplyTargetRejectsRootAndUnc()
{
    using var scope = new TempDirectory();
    var valid = System.IO.Path.Combine(scope.Path, "PaperNook.exe");
    UpdateApplyMode.ValidateTargetPath(valid);
    AssertThrows<InvalidDataException>(() => UpdateApplyMode.ValidateTargetPath(System.IO.Path.GetPathRoot(valid)!), "drive root");
    AssertThrows<InvalidDataException>(() => UpdateApplyMode.ValidateTargetPath(@"\\server\share\PaperNook.exe"), "UNC path");
}

static void UpdateHealthRequiresMatchingNonceTargetVersion()
{
    using var scope = new TempDirectory();
    var source = System.IO.Path.Combine(scope.Path, "PaperNook.download");
    var target = System.IO.Path.Combine(scope.Path, "PaperNook.exe");
    File.WriteAllBytes(source, [1, 2, 3, 4]);
    File.WriteAllBytes(target, [1, 2, 3, 4]);
    var store = new UpdateStateStore(scope.Path, DurableAtomicFileWriter.Shared);
    store.RegisterPendingApply(NewAsset("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", 4), source, target, "nonce-123");
    Assert(store.TryAuthorizeApply("nonce-123", target, out _), "pending update was not authorized");
    var health = new UpdateHealthStore(store);

    Assert(!health.MarkLaunchStarted("wrong", target, PaperTodoVersion.Parse("1.1.0")), "wrong nonce was accepted");
    Assert(!health.MarkLaunchStarted("nonce-123", target, PaperTodoVersion.Parse("1.2.0")), "wrong version was accepted");
    Assert(health.MarkLaunchStarted("nonce-123", target, PaperTodoVersion.Parse("1.1.0")), "matching launch was rejected");
    Assert(health.MarkLaunchHealthy("nonce-123", target, PaperTodoVersion.Parse("1.1.0")), "healthy launch was rejected");
    Assert(store.Load().PendingApply?.HealthyUtc != null, "healthy marker was not persisted");
}

static void InstallerPreparesExecutableCoordinator()
{
    using var scope = new TempDirectory();
    var download = System.IO.Path.Combine(scope.Path, "PaperNook.download");
    var target = System.IO.Path.Combine(scope.Path, "PaperNook.exe");
    File.WriteAllBytes(download, [1, 2, 3, 4]);
    File.WriteAllBytes(target, [9, 8, 7, 6]);
    var asset = NewAsset("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", 4);
    var verified = new VerifiedUpdate(asset, download, AuthenticodeVerification.Trusted("CN=PaperNook Test", new string('0', 64), "1.1.0"));
    var store = new UpdateStateStore(scope.Path, DurableAtomicFileWriter.Shared);
    var prepared = new UpdateInstaller(scope.Path, store).Prepare(verified, target);

    Assert(prepared.CoordinatorPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "coordinator is not executable");
    Assert(File.ReadAllBytes(prepared.CoordinatorPath).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "coordinator bytes changed");
    Assert(prepared.Nonce.Length >= 32, "nonce is too short");
    Assert(store.Load().PendingApply?.SourcePath == prepared.CoordinatorPath, "prepared apply was not registered");
}

static async Task VerifiedDownloadIsStaged()
{
    using var scope = new TempDirectory();
    var bytes = new byte[] { 1, 2, 3, 4 };
    var client = NewUpdateClient(scope.Path, bytes, AuthenticodeVerification.Trusted(
        "CN=PaperNook Test",
        new string('0', 64),
        "1.1.0"));
    var asset = NewAsset("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", bytes.Length);

    var verified = await client.DownloadAndVerifyAsync(asset, progress: null, CancellationToken.None);

    Assert(File.Exists(verified.DownloadPath), "verified download was not retained");
    Assert(File.ReadAllBytes(verified.DownloadPath).SequenceEqual(bytes), "staged bytes changed");
    Assert(verified.Asset.Version.ToString() == "1.1.0", "verified version changed");
}

static async Task HashMismatchRemovesDownload()
{
    using var scope = new TempDirectory();
    var client = NewUpdateClient(scope.Path, new byte[] { 1, 2, 3, 4 }, AuthenticodeVerification.Trusted(
        "CN=PaperNook Test",
        new string('0', 64),
        "1.1.0"));
    try
    {
        _ = await client.DownloadAndVerifyAsync(NewAsset(new string('0', 64), 4), null, CancellationToken.None);
        throw new InvalidOperationException("hash mismatch was accepted");
    }
    catch (InvalidDataException)
    {
    }
    Assert(!Directory.EnumerateFiles(scope.Path, "PaperNook.download", SearchOption.AllDirectories).Any(),
        "failed download was retained");
}

static async Task OversizedContentLengthIsRejected()
{
    using var scope = new TempDirectory();
    using var httpClient = new HttpClient(new StaticResponseHandler(
        new byte[] { 1 },
        ReleaseManifest.MaximumAssetLength + 1));
    var client = new UpdateClient(
        httpClient,
        scope.Path,
        new ReleaseTrust("CN=PaperNook Test", [new string('0', 64)]),
        new FakeAuthenticodeVerifier(AuthenticodeVerification.Trusted("CN=PaperNook Test", new string('0', 64), "1.1.0")));
    try
    {
        _ = await client.DownloadAndVerifyAsync(NewAsset(new string('0', 64), 1), null, CancellationToken.None);
        throw new InvalidOperationException("oversized response was accepted");
    }
    catch (InvalidDataException)
    {
    }
}

static async Task InterruptedApplyRecovers(UpdateApplyStage interruptedAt)
{
    using var scope = new TempDirectory();
    var oldBytes = new byte[] { 9, 8, 7, 6 };
    var newBytes = new byte[] { 1, 2, 3, 4 };
    var target = System.IO.Path.Combine(scope.Path, "PaperNook.exe");
    var source = System.IO.Path.Combine(scope.Path, "PaperNook.download");
    File.WriteAllBytes(target, oldBytes);
    File.WriteAllBytes(source, newBytes);
    var store = new UpdateStateStore(scope.Path, DurableAtomicFileWriter.Shared);
    store.RegisterPendingApply(NewAsset("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", 4), source, target, "nonce-123");
    Assert(store.TryAuthorizeApply("nonce-123", target, out var pending), "pending update was not authorized");

    try
    {
        await UpdateApplyEngine.ApplyAsync(
            store,
            pending,
            parentPid: 0,
            launchUpdatedTarget: _ => true,
            isLaunchHealthy: () => false,
            healthTimeout: TimeSpan.Zero,
            stageReached: stage =>
            {
                if (stage == interruptedAt) throw new InjectedInterruptionException(stage.ToString());
            },
            CancellationToken.None);
    }
    catch (InjectedInterruptionException)
    {
    }

    UpdateApplyEngine.RecoverInterruptedApply(store);
    Assert(File.Exists(target), $"target is missing after {interruptedAt}");
    var targetBytes = File.ReadAllBytes(target);
    Assert(targetBytes.SequenceEqual(oldBytes) || targetBytes.SequenceEqual(newBytes),
        $"target is truncated after {interruptedAt}");
}

static UpdateClient NewUpdateClient(string cacheDirectory, byte[] bytes, AuthenticodeVerification verification)
{
    var httpClient = new HttpClient(new StaticResponseHandler(bytes, bytes.Length));
    return new UpdateClient(
        httpClient,
        cacheDirectory,
        new ReleaseTrust("CN=PaperNook Test", [new string('0', 64)]),
        new FakeAuthenticodeVerifier(verification));
}

static ReleaseAsset NewAsset(string sha256, long length) => new(
    ReleaseDistribution.SelfContained,
    ValidFileName,
    PaperTodoVersion.Parse("1.1.0"),
    length,
    sha256,
    new Uri(ValidUrl));

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string scenario) where TException : Exception
{
    try
    {
        action();
        throw new InvalidOperationException($"Expected {typeof(TException).Name} for {scenario}.");
    }
    catch (TException)
    {
    }
}

internal sealed class StaticResponseHandler(byte[] content, long declaredLength) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentLength = declaredLength;
        return Task.FromResult(response);
    }
}

internal sealed class FakeAuthenticodeVerifier(AuthenticodeVerification verification) : IAuthenticodeVerifier
{
    public AuthenticodeVerification Verify(string path, ReleaseTrust trust) => verification;
}

internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "papertodo-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { }
    }
}

internal sealed class InjectedInterruptionException(string message) : Exception(message);
