using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace BattlefieldFrontlineCollector;

internal sealed record CollectorUploadResponse(bool Ok, string MatchUid, string? Realm,
    string? Status, string? PollUrl, double? MatchLogs, string? AnalysisUrl,
    IReadOnlyDictionary<string, RemoteScore>? PlayerScores,
    IReadOnlyDictionary<string, IReadOnlyList<BehaviorTendency>>? BehaviorTable,
    string? EvidenceNotice, string? Error);
internal sealed record CollectorDetailResponse(bool Ok, string MatchUid, string? Realm,
    string? Status, DetailedAnalysis? Detail, string? Error);
internal sealed record CollectorIntegrityCohortResponse(bool Ok, string? Schema,
    IReadOnlyList<IntegrityCohortSeries>? Series,
    IReadOnlyDictionary<string, IntegrityRealmCoverage>? RealmCoverage, string? Error);
internal sealed record CollectorIntegrityReferenceSnapshotResponse(bool Ok, string? Schema,
    string? ModelVersion, DateTimeOffset? GeneratedAtUtc, string? ContentHash,
    int DailySuccessfulDownloadLimit, IReadOnlyList<IntegrityCohortSeries>? Series,
    IReadOnlyDictionary<string, IntegrityRealmCoverage>? RealmCoverage, string? Error);
internal sealed record IntegrityReferenceCacheEnvelope(string? ETag,
    int? RemainingDownloadsToday, CollectorIntegrityReferenceSnapshotResponse Snapshot);
internal sealed record CollectorUploadStatusResponse(bool Ok, string MatchUid, string? Realm,
    bool Complete, int? ChunkCount, IReadOnlyList<int>? Received, string? Error);
internal sealed record UploadProgress(string Stage, long CompletedBytes, long TotalBytes,
    int CompletedChunks, int TotalChunks, double BytesPerSecond, TimeSpan Elapsed);
internal sealed record DetailFetchResult(HttpStatusCode StatusCode, string Body,
    byte[]? CompactPayload);
internal sealed record PluginRegistrationStartResponse(bool Ok, string? RequestId,
    string? ClaimSecret, string? BrowserUrl, int? ExpiresInSeconds, string? Error);
internal sealed record PluginRegistrationClaimResponse(bool Ok, string? Status, string? Token,
    string? KeyKind, string? Label, DateTimeOffset? ExpiresAt, string? Error);
internal sealed record CollectorMetaResponse(string? ServerVersion,
    IReadOnlyList<string>? ApiVersions, IReadOnlyList<int>? AcceptedCaptureSchemas,
    int DatabaseSchema, int RequiredDatabaseSchema, string? AnalysisVersion,
    IReadOnlyList<string>? AcceptedPackageEncodings = null,
    IReadOnlyList<string>? DetailFormats = null,
    IReadOnlyList<CollectorPublicCodebook>? PublicCodebooks = null);
internal sealed record CollectorPublicCodebook(string? Kind, int Version, string? Sha256);
internal sealed record CollectorReadyResponse(bool Ok, string? Code,
    int RequiredSchema, int ActualSchema);
internal sealed record ServerCompatibilityState(bool Checked, bool Available,
    bool Maintenance, string Code, string? ServerVersion);

internal sealed class RateLimitException(TimeSpan retryAfter, string message) : Exception(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

internal sealed class UploadService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string IntegritySnapshotSchema = "battlefield-integrity-reference-snapshot/v2";
    internal const string CurrentIntegrityDistributionModel = "observer-bounded-integrity-distributions-v16";
    internal const string CurrentAnomalyModel = "observer-bounded-anomaly-evidence-v15";
    private readonly Configuration configuration;
    private readonly MatchRepository repository;
    private readonly IPluginLog log;
    private readonly string integrityReferenceCachePath;
    private readonly string sharedIntegrityReferenceCachePath;
    private readonly string machineId;
    private readonly Func<string?> gameAccountId;
    private readonly (int Version, string Sha256) publicCaptureCodebook;
    private readonly (int Version, string Sha256) publicDetailCodebook;
    private readonly string assetRoot;
    private readonly HttpClient directClient = CreateDirectHttpClient();
    private readonly HttpClient publicClient = CreatePublicHttpClient();
    private readonly ConcurrentDictionary<string, UploadProgress> uploadProgress = new();
    private readonly ConcurrentDictionary<string, int> retryAttempts = new();
    private readonly ConcurrentDictionary<string, Task> processingMonitors = new();
    private readonly Task compatibilityProbe;
    private volatile ServerCompatibilityState serverCompatibility =
        new(false, false, false, "checking", null);
    private volatile bool serverSupportsBfcap;
    private volatile bool serverSupportsBfdetail;
    private string CollectorToken => configuration.GetCollectorToken();

    private static HttpClient CreateDirectHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = ConnectDirectAsync,
        // The collector is a user-owned LAN service. Do not route its token or
        // match payload through Windows/Clash system proxies: hostnames such as
        // `*.local` are not consistently covered by the system's <local> rule
        // and may otherwise return a proxy-generated 502 Bad Gateway.
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8
        };
        return CreateCollectorClient(handler);
    }

    private static HttpClient CreatePublicHttpClient()
    {
        // Public HTTPS collectors may only be reachable through the user's
        // Windows system proxy (for example Clash). Keep the normal .NET proxy
        // and TLS path for public hosts; bearer tokens remain inside HTTPS.
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8
        };
        return CreateCollectorClient(handler);
    }

    private static HttpClient CreateCollectorClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        // Cloudflare Browser Integrity rejects an empty/default .NET signature
        // with error 1010. A stable product identifier also makes legitimate
        // collector traffic auditable without exposing player information.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BattlefieldFrontlineCollector/0.8.105");
        return client;
    }

    private HttpClient PrimaryClientFor(Uri? uri) => directClient;

    private HttpClient? FallbackClientFor(Uri? uri)
        => uri != null && !IsPrivateCollectorHost(uri.Host) ? publicClient : null;

    private static void PreferHttp2(HttpRequestMessage request)
    {
        if (request.RequestUri?.Scheme == Uri.UriSchemeHttps &&
            !IsPrivateCollectorHost(request.RequestUri.Host))
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }
    }

    private static bool IsPrivateCollectorHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            !host.Contains('.')) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    private static async ValueTask<Stream> ConnectDirectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        // The Debian host advertises both families, but the LAN firewall may
        // permit port 8879 only over IPv4. Prefer IPv4 and retain IPv6 fallback.
        foreach (var address in addresses.OrderBy(address =>
                     address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException(
            $"Could not connect directly to collector host {context.DnsEndPoint.Host}.",
            lastError);
    }
    private readonly ConcurrentQueue<(string MatchUid, string Path, string Realm)> queue = new();
    private readonly ConcurrentQueue<(string LocalMatchUid, string RemoteMatchUid, string Realm)> detailQueue = new();
    private readonly ConcurrentQueue<(string Key, string Url)> cohortQueue = new();
    private readonly ConcurrentDictionary<string, byte> pending = new();
    private readonly ConcurrentDictionary<string, byte> pendingDetails = new();
    private readonly ConcurrentDictionary<string, byte> pendingCohorts = new();
    private readonly ConcurrentDictionary<string, IntegrityCohortSnapshot> cohortCache = new();
    private readonly ConcurrentQueue<byte> integrityReferenceQueue = new();
    private int integrityReferencePending;
    private volatile IntegrityReferenceCacheEnvelope? integrityReferenceCache;
    private volatile string? integrityReferenceError;
    private readonly SemaphoreSlim wake = new(0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;

    public UploadService(Configuration configuration, MatchRepository repository, IPluginLog log,
        string cacheDirectory, Func<string?> gameAccountId, string assetRoot)
    {
        this.configuration = configuration;
        this.repository = repository;
        this.log = log;
        machineId = MachineIdentity.GetStableId(configuration);
        this.gameAccountId = gameAccountId;
        this.assetRoot = assetRoot;
        publicCaptureCodebook = BfcapCodec.CodebookIdentity(assetRoot);
        publicDetailCodebook = BfdetailCodec.CodebookIdentity(assetRoot);
        Directory.CreateDirectory(cacheDirectory);
        integrityReferenceCachePath = Path.Combine(cacheDirectory, "integrity-reference-snapshot.json");
        sharedIntegrityReferenceCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BattlefieldFrontlineCollector", "cache", "integrity-reference-snapshot.json");
        integrityReferenceCache = LoadIntegrityReferenceCache(integrityReferenceCachePath) ??
                                  LoadIntegrityReferenceCache(sharedIntegrityReferenceCachePath);
        compatibilityProbe = RefreshServerCompatibilityAsync();
        worker = Task.Run(WorkerAsync);
        // One full anonymous snapshot supplies every ordinary gray reference
        // chart. Fetch it once on first use; later updates remain explicitly
        // user-controlled and never fan out into per-card requests.
        if (integrityReferenceCache == null && !string.IsNullOrWhiteSpace(CollectorToken))
            RefreshIntegrityReferenceSnapshot();
    }

    public void Enqueue(string matchUid, string path, string realm)
    {
        if (!pending.TryAdd(matchUid, 0)) return;
        queue.Enqueue((matchUid, path, realm));
        wake.Release();
    }

    public bool TryGetUploadProgress(string matchUid, out UploadProgress progress)
        => uploadProgress.TryGetValue(matchUid, out progress!);

    public ServerCompatibilityState ServerCompatibility => serverCompatibility;

    public string MachineBindingId => machineId;

    public async Task<PluginRegistrationStartResponse> StartPluginRegistrationAsync(
        string keyKind, string bindingId, string label, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(configuration.ServerBaseUrl.TrimEnd('/') +
                               "/api/auth/plugin-registration/start");
        var payload = JsonSerializer.Serialize(new
        {
            keyKind,
            bindingId,
            label
        }, JsonOptions);
        var (status, body) = await SendRegistrationJsonWithFallbackAsync(
            endpoint, payload, cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<PluginRegistrationStartResponse>(body, JsonOptions)
                     ?? new PluginRegistrationStartResponse(false, null, null, null, null,
                         "registration response invalid");
        if (((int)status < 200 || (int)status >= 300) && parsed.Ok)
            return parsed with { Ok = false, Error = $"HTTP {(int)status}" };
        return parsed;
    }

    public async Task<PluginRegistrationClaimResponse> ClaimPluginRegistrationAsync(
        string requestId, string claimSecret, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(configuration.ServerBaseUrl.TrimEnd('/') +
                               "/api/auth/plugin-registration/claim");
        var payload = JsonSerializer.Serialize(new { requestId, claimSecret }, JsonOptions);
        var (status, body) = await SendRegistrationJsonWithFallbackAsync(
            endpoint, payload, cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<PluginRegistrationClaimResponse>(body, JsonOptions)
                     ?? new PluginRegistrationClaimResponse(false, null, null, null, null, null,
                         "claim response invalid");
        if (status == HttpStatusCode.Accepted) return parsed;
        if (((int)status < 200 || (int)status >= 300) && parsed.Ok)
            return parsed with { Ok = false, Error = $"HTTP {(int)status}" };
        return parsed;
    }

    private async Task<(HttpStatusCode StatusCode, string Body)>
        SendRegistrationJsonWithFallbackAsync(Uri endpoint, string payload,
            CancellationToken cancellationToken)
    {
        try
        {
            return await SendRegistrationJsonAttemptAsync(PrimaryClientFor(endpoint), endpoint,
                payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (FallbackClientFor(endpoint) is { } fallback &&
                                   IsRetryableTransportFailure(ex))
        {
            log.Warning(ex, "Direct plugin registration request failed; retrying via system proxy");
            return await SendRegistrationJsonAttemptAsync(fallback, endpoint, payload,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)>
        SendRegistrationJsonAttemptAsync(HttpClient client, Uri endpoint, string payload,
            CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        PreferHttp2(request);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode,
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public void EnqueueDetails(string localMatchUid, string remoteMatchUid, string realm)
    {
        if (!pendingDetails.TryAdd(localMatchUid, 0)) return;
        repository.SetDetailError(localMatchUid, null);
        detailQueue.Enqueue((localMatchUid, remoteMatchUid, realm));
        wake.Release();
    }

    public IntegrityCohortSnapshot GetIntegrityCohort(string realm, string excludeMatchUid,
        AnomalyCandidate candidate, string? behaviorKey = null, double? behaviorMax = null,
        bool requestIfMissing = true)
    {
        var kind = candidate.Kind switch
        {
            "movement_envelope_exceeded" => "movement_speed_active_unmounted_normalized",
            "action_range_outlier" => "action_range",
            "animation_lock_triple_weave" => "animation_lock_deficit",
            "local_animation_lock_bypass" => "local_request_lock_remaining",
            "local_cast_time_shortening" => "local_cast_completion_ratio",
            "local_lb_aftercast_cancel" => "local_lb_aftercast_lock_deficit",
            _ => candidate.Kind
        };
        // Movement depends on observed speed states, while Purify is shared by
        // every PvP job. Other action timing/range cohorts remain job/action scoped.
        var crossJob = kind is "movement_speed_active_unmounted_normalized" or
            "movement_speed_active_mounted_normalized" or "minimum_latency_purify" or
            "danger_response_recuperate" or "danger_response_purify" or "danger_response_guard";
        var scopedJob = crossJob ? (byte)0 : candidate.ClassJobId;
        // Job-independent does not mean action-independent. Recuperate, Purify and Guard
        // are pooled across jobs, but the stored anonymous snapshot still keeps their
        // distinct Action IDs. Zeroing the action here made those three gray references
        // impossible to match.
        var scopedAction = candidate.ActionId;
        const string cohortScope = "ALL";
        var key = string.Join('|', cohortScope, excludeMatchUid, scopedJob, kind,
            scopedAction, candidate.EvidenceScope, behaviorKey ?? "", behaviorMax?.ToString("0.####",
                System.Globalization.CultureInfo.InvariantCulture) ?? "");
        if (cohortCache.TryGetValue(key, out var cached)) return cached;
        // The manually refreshed anonymous snapshot is intentionally inclusive
        // of the current match and is used for visual context only. Candidate
        // publication remains server-side and never depends on this cache.
        if (string.IsNullOrWhiteSpace(behaviorKey) && integrityReferenceCache?.Snapshot is { Ok: true } reference)
        {
            var series = (reference.Series ?? []).Where(row => row.Kind == kind &&
                    row.ClassJobId == scopedJob && row.ActionId == scopedAction &&
                    string.Equals(row.EvidenceScope, candidate.EvidenceScope,
                        StringComparison.Ordinal))
                .OrderByDescending(row => row.Observations).FirstOrDefault();
            if (series != null)
                return new IntegrityCohortSnapshot(key, false, series, reference.RealmCoverage);
        }
        // The integrity dashboard can contain dozens of job/action cards. Merely drawing the
        // page must not fan out into dozens of server calls. Callers first peek at the stable
        // key, then explicitly opt in for the one reference distribution the user opens.
        if (!requestIfMissing)
            return new IntegrityCohortSnapshot(key, false, Error: "not_requested");
        if (pendingCohorts.TryAdd(key, 0))
        {
            var query = $"kind={Uri.EscapeDataString(kind)}";
            if (scopedJob != 0) query += $"&classJobId={scopedJob}";
            query += $"&excludeMatchUid={Uri.EscapeDataString(excludeMatchUid)}";
            if (scopedAction != 0) query += $"&actionId={scopedAction}";
            if (!string.IsNullOrWhiteSpace(behaviorKey))
                query += $"&behaviorKey={Uri.EscapeDataString(behaviorKey)}";
            if (behaviorMax is { } maximum)
                query += "&behaviorMax=" + maximum.ToString("0.####",
                    System.Globalization.CultureInfo.InvariantCulture);
            cohortQueue.Enqueue((key, configuration.ServerBaseUrl.TrimEnd('/') +
                $"/api/collector/v2/integrity/cohorts/{cohortScope}?{query}"));
            cohortCache[key] = new IntegrityCohortSnapshot(key, true);
            wake.Release();
        }
        return cohortCache.TryGetValue(key, out cached)
            ? cached : new IntegrityCohortSnapshot(key, true);
    }

    public void InvalidateIntegrityCohort(string key)
    {
        // Failed snapshots are cached so a broken connection cannot hammer the server every
        // frame. The UI exposes an explicit retry which clears only the selected cohort.
        if (!pendingCohorts.ContainsKey(key)) cohortCache.TryRemove(key, out _);
    }

    public IntegrityReferenceSnapshotState GetIntegrityReferenceSnapshotState()
    {
        var cached = integrityReferenceCache;
        return new IntegrityReferenceSnapshotState(cached != null,
            Volatile.Read(ref integrityReferencePending) != 0,
            cached?.Snapshot.GeneratedAtUtc, cached?.Snapshot.ModelVersion,
            cached?.RemainingDownloadsToday,
            cached?.Snapshot.DailySuccessfulDownloadLimit ?? 2,
            cached?.Snapshot.Series?.Count ?? 0, integrityReferenceError);
    }

    public void RefreshIntegrityReferenceSnapshot()
    {
        if (Interlocked.CompareExchange(ref integrityReferencePending, 1, 0) != 0) return;
        integrityReferenceError = null;
        integrityReferenceQueue.Enqueue(0);
        wake.Release();
    }

    public void ResumePending()
    {
        if (!configuration.AutoUpload) return;
        foreach (var match in repository.Snapshot())
            if (match.UploadState is not "complete" && File.Exists(match.CapturePath))
                Enqueue(match.MatchUid, match.CapturePath,
                    string.IsNullOrWhiteSpace(match.Realm) ? configuration.Realm : match.Realm);
    }

    private async Task RefreshServerCompatibilityAsync()
    {
        try
        {
            var root = configuration.ServerBaseUrl.TrimEnd('/');
            var metaEndpoint = new Uri(root + "/api/meta");
            var (metaStatus, metaBody) = await SendPublicGetWithFallbackAsync(metaEndpoint)
                .ConfigureAwait(false);
            if ((int)metaStatus is < 200 or >= 300)
            {
                serverCompatibility = new(true, false,
                    metaStatus == HttpStatusCode.ServiceUnavailable,
                    $"meta_http_{(int)metaStatus}", null);
                return;
            }
            var meta = JsonSerializer.Deserialize<CollectorMetaResponse>(metaBody, JsonOptions);
            if (meta?.ApiVersions?.Contains("v2") != true ||
                meta.AcceptedCaptureSchemas?.Contains(2) != true)
            {
                serverCompatibility = new(true, false, false, "server_incompatible",
                    meta?.ServerVersion);
                return;
            }
            serverSupportsBfcap = meta.AcceptedPackageEncodings?.Contains(
                BfcapCodec.PackageEncoding, StringComparer.OrdinalIgnoreCase) == true &&
                meta.PublicCodebooks?.Any(codebook =>
                    string.Equals(codebook.Kind, "capture", StringComparison.OrdinalIgnoreCase) &&
                    codebook.Version == publicCaptureCodebook.Version &&
                    string.Equals(codebook.Sha256, publicCaptureCodebook.Sha256,
                        StringComparison.OrdinalIgnoreCase)) == true;
            serverSupportsBfdetail = meta.DetailFormats?.Contains("bfdetail-zstd-v1",
                StringComparer.OrdinalIgnoreCase) == true &&
                meta.PublicCodebooks?.Any(codebook =>
                    string.Equals(codebook.Kind, "detail", StringComparison.OrdinalIgnoreCase) &&
                    codebook.Version == publicDetailCodebook.Version &&
                    string.Equals(codebook.Sha256, publicDetailCodebook.Sha256,
                        StringComparison.OrdinalIgnoreCase)) == true;
            var readyEndpoint = new Uri(root + "/ready");
            var (readyStatus, readyBody) = await SendPublicGetWithFallbackAsync(readyEndpoint)
                .ConfigureAwait(false);
            var ready = JsonSerializer.Deserialize<CollectorReadyResponse>(readyBody, JsonOptions);
            if ((int)readyStatus is >= 200 and < 300 && ready?.Ok == true)
                serverCompatibility = new(true, true, false, "ready", meta.ServerVersion);
            else
                serverCompatibility = new(true, false, true,
                    ready?.Code ?? $"ready_http_{(int)readyStatus}", meta.ServerVersion);
        }
        catch (Exception ex)
        {
            serverCompatibility = new(true, false, false, UploadErrorCode(ex), null);
            log.Debug(ex, "Collector compatibility probe failed");
        }
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendPublicGetWithFallbackAsync(
        Uri endpoint)
    {
        async Task<(HttpStatusCode, string)> Attempt(HttpClient client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            PreferHttp2(request);
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
            return (response.StatusCode,
                await response.Content.ReadAsStringAsync(stop.Token).ConfigureAwait(false));
        }
        try { return await Attempt(PrimaryClientFor(endpoint)).ConfigureAwait(false); }
        catch (Exception ex) when (FallbackClientFor(endpoint) is { } fallback &&
                                   IsRetryableTransportFailure(ex))
        {
            return await Attempt(fallback).ConfigureAwait(false);
        }
    }

    private async Task WorkerAsync()
    {
        try { await compatibilityProbe.ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await wake.WaitAsync(stop.Token); }
            catch (OperationCanceledException) { break; }
            while (queue.TryDequeue(out var item))
            {
                if (!configuration.AutoUpload) { pending.TryRemove(item.MatchUid, out _); continue; }
                var completed = false;
                try { completed = await UploadOneAsync(item.MatchUid, item.Path, item.Realm); }
                finally { pending.TryRemove(item.MatchUid, out _); }
                if (completed) retryAttempts.TryRemove(item.MatchUid, out _);
                else if (configuration.AutoUpload && File.Exists(item.Path) &&
                         !string.IsNullOrWhiteSpace(CollectorToken))
                    ScheduleRetry(item.MatchUid, item.Path, item.Realm);
            }
            while (detailQueue.TryDequeue(out var detail))
            {
                try { await DownloadDetailsAsync(detail.LocalMatchUid, detail.RemoteMatchUid, detail.Realm); }
                finally { pendingDetails.TryRemove(detail.LocalMatchUid, out _); }
            }
            while (cohortQueue.TryDequeue(out var cohort))
            {
                try { await DownloadCohortAsync(cohort.Key, cohort.Url); }
                finally { pendingCohorts.TryRemove(cohort.Key, out _); }
            }
            while (integrityReferenceQueue.TryDequeue(out _))
            {
                try { await DownloadIntegrityReferenceSnapshotAsync(); }
                finally { Interlocked.Exchange(ref integrityReferencePending, 0); }
            }
        }
    }

    private void ScheduleRetry(string matchUid, string path, string realm)
    {
        var attempt = retryAttempts.AddOrUpdate(matchUid, 1, (_, current) => current + 1);
        var delay = TimeSpan.FromSeconds(Math.Min(900,
            5 * Math.Pow(2, Math.Min(attempt - 1, 8))));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, stop.Token).ConfigureAwait(false);
                if (!stop.IsCancellationRequested && configuration.AutoUpload && File.Exists(path))
                    Enqueue(matchUid, path, realm);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }

    private IntegrityReferenceCacheEnvelope? LoadIntegrityReferenceCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var envelope = JsonSerializer.Deserialize<IntegrityReferenceCacheEnvelope>(
                File.ReadAllText(path), JsonOptions);
            if (envelope?.Snapshot.Schema != IntegritySnapshotSchema ||
                envelope.Snapshot.ModelVersion != CurrentIntegrityDistributionModel ||
                envelope.Snapshot.Series == null || string.IsNullOrWhiteSpace(envelope.ETag))
                return null;
            return envelope;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Integrity reference cache could not be read");
            return null;
        }
    }

    private void SaveIntegrityReferenceCache(IntegrityReferenceCacheEnvelope envelope)
    {
        var serialized = JsonSerializer.Serialize(envelope, JsonOptions);
        SaveIntegrityReferenceCacheFile(integrityReferenceCachePath, serialized);
        // CN and international launchers use separate plugin configuration folders, while
        // this snapshot is an anonymous ALL-realm reference. Keep one shared local copy so
        // both installations can draw gray bars without spending another daily download.
        SaveIntegrityReferenceCacheFile(sharedIntegrityReferenceCachePath, serialized);
    }

    private static void SaveIntegrityReferenceCacheFile(string path, string serialized)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, serialized);
        File.Move(temporary, path, true);
    }

    private async Task DownloadIntegrityReferenceSnapshotAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CollectorToken))
                throw new InvalidOperationException("missing_token");
            var url = configuration.ServerBaseUrl.TrimEnd('/') +
                      "/api/collector/v2/matches/integrity-reference-snapshot";
            var response = await SendAuthorizedSnapshotGetAsync(url, integrityReferenceCache?.ETag);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (integrityReferenceCache is { } existing)
                {
                    var unchanged = existing with
                    { RemainingDownloadsToday = response.RemainingDownloadsToday };
                    await Task.Run(() => SaveIntegrityReferenceCache(unchanged), stop.Token);
                    integrityReferenceCache = unchanged;
                }
                return;
            }
            if ((int)response.StatusCode is < 200 or >= 300)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.Body}");
            var snapshot = JsonSerializer.Deserialize<CollectorIntegrityReferenceSnapshotResponse>(
                               response.Body, JsonOptions)
                           ?? throw new InvalidDataException("Server returned an empty integrity snapshot.");
            if (!snapshot.Ok || snapshot.Series == null)
                throw new InvalidDataException(snapshot.Error ?? "Integrity snapshot query failed.");
            if (snapshot.Schema != IntegritySnapshotSchema ||
                snapshot.ModelVersion != CurrentIntegrityDistributionModel ||
                string.IsNullOrWhiteSpace(snapshot.ContentHash) || snapshot.ContentHash.Length != 64)
                throw new InvalidDataException("Integrity snapshot version contract mismatch.");
            if (string.IsNullOrWhiteSpace(response.BodySha256) ||
                !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response.Body)))
                    .Equals(response.BodySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Integrity snapshot body hash mismatch.");
            var envelope = new IntegrityReferenceCacheEnvelope(response.ETag,
                response.RemainingDownloadsToday, snapshot);
            await Task.Run(() => SaveIntegrityReferenceCache(envelope), stop.Token);
            integrityReferenceCache = envelope;
            cohortCache.Clear();
            integrityReferenceError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            integrityReferenceError = UploadErrorCode(ex);
            log.Warning(ex, "Integrity reference snapshot download failed");
        }
    }

    private async Task DownloadCohortAsync(string key, string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CollectorToken))
                throw new InvalidOperationException("missing_token");
            var (statusCode, body) = await SendAuthorizedGetAsync(url);
            if ((int)statusCode is < 200 or >= 300)
                throw new InvalidOperationException($"HTTP {(int)statusCode}: {body}");
            var result = JsonSerializer.Deserialize<CollectorIntegrityCohortResponse>(body, JsonOptions)
                         ?? throw new InvalidDataException("Server returned an empty cohort response.");
            if (!result.Ok) throw new InvalidDataException(result.Error ?? "Cohort query failed.");
            cohortCache[key] = new IntegrityCohortSnapshot(key, false,
                result.Series?.OrderByDescending(x => x.Observations).FirstOrDefault(),
                result.RealmCoverage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            cohortCache[key] = new IntegrityCohortSnapshot(key, false, null, null, UploadErrorCode(ex));
            log.Warning(ex, "Integrity cohort download failed for {Key}", key);
        }
    }

    private async Task DownloadDetailsAsync(string localMatchUid, string remoteMatchUid, string realm)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CollectorToken))
                throw new InvalidOperationException("missing_token");
            var fetched = await FetchDetailsAsync(remoteMatchUid, realm);
            var statusCode = fetched.StatusCode;
            var body = fetched.Body;
            if (UploadRecoveryPolicy.DetailAccessMustBeReestablished(statusCode, body) &&
                repository.Snapshot().FirstOrDefault(x => x.MatchUid == localMatchUid) is { } local &&
                File.Exists(local.CapturePath))
            {
                // A match can have been uploaded with a legacy/operator key before the
                // current Discord API key existed.  The server intentionally hides such
                // inaccessible rows behind 404.  An explicit detail request may safely
                // replay the local capture: upload is idempotent and only adds the current
                // user's access row for an already-known perspective.
                log.Information(
                    "Detailed analysis access is missing for {MatchUid}; re-establishing it from local capture",
                    remoteMatchUid);
                if (await UploadOneAsync(localMatchUid, local.CapturePath, realm,
                        waitForAnalysis: true))
                {
                    var refreshed = repository.Snapshot()
                        .FirstOrDefault(x => x.MatchUid == localMatchUid);
                    remoteMatchUid = refreshed?.RemoteMatchUid ?? remoteMatchUid;
                    fetched = await FetchDetailsAsync(remoteMatchUid, realm);
                    statusCode = fetched.StatusCode;
                    body = fetched.Body;
                }
            }
            if ((int)statusCode is < 200 or >= 300)
                throw new InvalidOperationException($"HTTP {(int)statusCode}: {body}");
            var result = JsonSerializer.Deserialize<CollectorDetailResponse>(body, JsonOptions)
                         ?? throw new InvalidDataException("Server returned an empty detail response.");
            if (!result.Ok || result.Detail == null)
                throw new InvalidDataException(result.Error ?? "Server returned no detail analysis.");
            repository.MergeDetail(localMatchUid, result.Detail, fetched.CompactPayload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            repository.SetDetailError(localMatchUid, UploadErrorCode(ex));
            log.Warning(ex, "Detailed analysis download failed for {MatchUid}", remoteMatchUid);
        }
    }

    private Task<DetailFetchResult> FetchDetailsAsync(
        string remoteMatchUid, string realm)
    {
        var url = configuration.ServerBaseUrl.TrimEnd('/') +
                  $"/api/collector/v2/matches/{realm}/{remoteMatchUid}/details";
        return SendAuthorizedDetailGetAsync(url);
    }

    private async Task<DetailFetchResult> SendAuthorizedDetailGetAsync(string url)
    {
        var endpoint = new Uri(url);
        try { return await SendAuthorizedDetailGetWithCodecFallbackAsync(
            PrimaryClientFor(endpoint), endpoint); }
        catch (Exception ex) when (FallbackClientFor(endpoint) is { } fallback &&
                                   IsRetryableTransportFailure(ex))
        {
            log.Warning(ex,"Direct BFDETAIL GET failed for {Url}; retrying via system proxy",url);
            return await SendAuthorizedDetailGetWithCodecFallbackAsync(fallback, endpoint);
        }
    }

    private async Task<DetailFetchResult> SendAuthorizedDetailGetWithCodecFallbackAsync(
        HttpClient client, Uri endpoint)
    {
        try
        {
            return await SendAuthorizedDetailGetAttemptAsync(client, endpoint,
                preferCompact: true);
        }
        catch (InvalidDataException exception) when (serverSupportsBfdetail)
        {
            // Compact transport is optional. If cross-runtime canonicalization
            // ever differs, retry the authenticated resource as ordinary JSON
            // instead of making the detail page unavailable.
            // 紧凑格式不兼容时自动回退普通 JSON，无需重新上传战局。
            log.Warning(exception,
                "BFDETAIL decode failed for {Url}; retrying as ordinary JSON", endpoint);
            return await SendAuthorizedDetailGetAttemptAsync(client, endpoint,
                preferCompact: false);
        }
    }

    private async Task<DetailFetchResult> SendAuthorizedDetailGetAttemptAsync(
        HttpClient client, Uri endpoint, bool preferCompact)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);
        PreferHttp2(request); AddAuthorization(request);
        if (serverSupportsBfdetail && preferCompact)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(BfdetailCodec.MediaType));
        else
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,stop.Token);
        var bytes=await response.Content.ReadAsByteArrayAsync(stop.Token);
        if (IsRetryableGatewayStatus(response.StatusCode))
            throw new InvalidOperationException(
                $"HTTP {(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");
        var contentType=response.Content.Headers.ContentType?.MediaType;
        var isCompact=string.Equals(contentType,BfdetailCodec.MediaType,StringComparison.OrdinalIgnoreCase);
        var body=isCompact ? BfdetailCodec.DecodeToJson(bytes,assetRoot) : Encoding.UTF8.GetString(bytes);
        return new DetailFetchResult(response.StatusCode,body,isCompact ? bytes : null);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendAuthorizedGetAsync(string url)
    {
        var endpoint = new Uri(url);
        try
        {
            return await SendAuthorizedGetAttemptAsync(PrimaryClientFor(endpoint), endpoint);
        }
        catch (Exception ex) when (FallbackClientFor(endpoint) is { } fallback &&
                                   IsRetryableTransportFailure(ex))
        {
            log.Warning(ex, "Direct Cloudflare GET failed for {Url}; retrying via system proxy", url);
            return await SendAuthorizedGetAttemptAsync(fallback, endpoint);
        }
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendAuthorizedGetAttemptAsync(
        HttpClient client, Uri endpoint)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        PreferHttp2(request);
        AddAuthorization(request);
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, stop.Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(stop.Token));
    }

    private async Task<(HttpStatusCode StatusCode, string Body, string? ETag,
        int? RemainingDownloadsToday, string? BodySha256)> SendAuthorizedSnapshotGetAsync(string url, string? etag)
    {
        var endpoint = new Uri(url);
        try
        {
            return await SendAuthorizedSnapshotGetAttemptAsync(PrimaryClientFor(endpoint), endpoint, etag);
        }
        catch (Exception ex) when (FallbackClientFor(endpoint) is { } fallback &&
                                   IsRetryableTransportFailure(ex))
        {
            log.Warning(ex, "Direct snapshot GET failed for {Url}; retrying via system proxy", url);
            return await SendAuthorizedSnapshotGetAttemptAsync(fallback, endpoint, etag);
        }
    }

    private async Task<(HttpStatusCode StatusCode, string Body, string? ETag,
        int? RemainingDownloadsToday, string? BodySha256)> SendAuthorizedSnapshotGetAttemptAsync(
        HttpClient client, Uri endpoint, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        PreferHttp2(request);
        AddAuthorization(request);
        request.Headers.TryAddWithoutValidation("X-Integrity-Model-Version",
            CurrentIntegrityDistributionModel);
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, stop.Token);
        int? remaining = response.Headers.TryGetValues("X-Integrity-Snapshot-Remaining", out var values) &&
                         int.TryParse(values.FirstOrDefault(), out var parsed) ? parsed : null;
        var bodyHash = response.Headers.TryGetValues("X-Integrity-Snapshot-Body-SHA256",
            out var bodyHashes) ? bodyHashes.FirstOrDefault() : null;
        return (response.StatusCode,
            response.StatusCode == HttpStatusCode.NotModified
                ? string.Empty : await response.Content.ReadAsStringAsync(stop.Token),
            response.Headers.ETag?.ToString(), remaining, bodyHash);
    }

    private async Task<bool> UploadOneAsync(string matchUid, string path, string realm,
        bool waitForAnalysis = false)
    {
        repository.SetUploadState(matchUid, "uploading");
        var totalTimer = Stopwatch.StartNew();
        uploadProgress[matchUid] = new UploadProgress("compressing", 0,
            new FileInfo(path).Length, 0, 0, 0, TimeSpan.Zero);
        string? compressedPath = null;
        var ownsTemporaryPackage = false;
        var packageEncoding = "gzip";
        var completed = false;
        try
        {
            if (string.IsNullOrWhiteSpace(CollectorToken))
                throw new InvalidOperationException("missing_token");
            if (!serverCompatibility.Available)
                await RefreshServerCompatibilityAsync().ConfigureAwait(false);
            if (!serverCompatibility.Available)
                throw new InvalidOperationException(serverCompatibility.Maintenance
                    ? "server_maintenance" : serverCompatibility.Code);
            if (BfcapCodec.IsBfcap(path))
            {
                if (!serverSupportsBfcap)
                    throw new InvalidOperationException("server_bfcap_codebook_outdated");
                compressedPath = path;
                packageEncoding = BfcapCodec.PackageEncoding;
            }
            else
            {
                compressedPath = await CompressUploadAsync(path, stop.Token);
                ownsTemporaryPackage = true;
            }
            var endpoint = new Uri(configuration.ServerBaseUrl.TrimEnd('/') +
                                   "/api/collector/v2/matches");
            var result = await SendChunkedUploadAsync(endpoint, compressedPath, matchUid, realm,
                packageEncoding);
            if (!result.Ok) throw new InvalidDataException(result.Error ?? "Server rejected the match.");
            repository.SetUploadState(matchUid, "processing");
            var packageBytes = new FileInfo(compressedPath).Length;
            uploadProgress[matchUid] = new UploadProgress("processing", packageBytes,
                packageBytes, 0, 0, packageBytes / Math.Max(.001, totalTimer.Elapsed.TotalSeconds),
                totalTimer.Elapsed);
            // Safe package acceptance and CPU-heavy analysis are different stages.
            // Do not hold the only upload/detail worker while the server's two
            // analysis workers parse this match. A bounded per-match monitor keeps
            // the durable "processing" state and publishes scores when ready.
            var pollUrl = result.PollUrl ??
                          $"/api/collector/v2/matches/{realm}/{matchUid}";
            if (waitForAnalysis)
            {
                await PollUntilReadyAsync(matchUid, pollUrl).ConfigureAwait(false);
                uploadProgress[matchUid] = new UploadProgress("complete", packageBytes,
                    packageBytes, 0, 0,
                    packageBytes / Math.Max(.001, totalTimer.Elapsed.TotalSeconds),
                    totalTimer.Elapsed);
            }
            else
            {
                StartProcessingMonitor(matchUid, path, realm, pollUrl,
                    packageBytes, totalTimer.Elapsed);
            }
            completed = true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            repository.SetUploadState(matchUid, "processing");
        }
        catch (Exception ex)
        {
            repository.SetUploadState(matchUid, "failed", UploadErrorCode(ex));
            var previous = uploadProgress.GetValueOrDefault(matchUid);
            uploadProgress[matchUid] = new UploadProgress("failed",
                previous?.CompletedBytes ?? 0, previous?.TotalBytes ?? 0,
                previous?.CompletedChunks ?? 0, previous?.TotalChunks ?? 0,
                previous?.BytesPerSecond ?? 0, totalTimer.Elapsed);
            log.Warning(ex, "Collector upload failed for {MatchUid}", matchUid);
        }
        finally
        {
            if (ownsTemporaryPackage && !string.IsNullOrWhiteSpace(compressedPath))
            {
                try { File.Delete(compressedPath); }
                catch (Exception ex) { log.Debug(ex, "Could not remove temporary upload package {Path}", compressedPath); }
            }
        }
        return completed;
    }

    private void StartProcessingMonitor(string matchUid, string path, string realm,
        string pollUrl, long packageBytes, TimeSpan acceptedElapsed)
    {
        processingMonitors.GetOrAdd(matchUid, ignoredMatchUid => Task.Run(async () =>
        {
            try
            {
                await PollUntilReadyAsync(matchUid, pollUrl).ConfigureAwait(false);
                uploadProgress[matchUid] = new UploadProgress("complete", packageBytes,
                    packageBytes, 0, 0,
                    packageBytes / Math.Max(.001, acceptedElapsed.TotalSeconds), acceptedElapsed);
                retryAttempts.TryRemove(matchUid, out _);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                repository.SetUploadState(matchUid, "processing");
            }
            catch (Exception ex)
            {
                repository.SetUploadState(matchUid, "failed", UploadErrorCode(ex));
                uploadProgress[matchUid] = new UploadProgress("failed", packageBytes,
                    packageBytes, 0, 0,
                    packageBytes / Math.Max(.001, acceptedElapsed.TotalSeconds), acceptedElapsed);
                log.Warning(ex, "Collector analysis monitor failed for {MatchUid}", matchUid);
                if (configuration.AutoUpload && File.Exists(path) &&
                    !string.IsNullOrWhiteSpace(CollectorToken))
                    ScheduleRetry(matchUid, path, realm);
            }
            finally
            {
                processingMonitors.TryRemove(matchUid, out _);
            }
        }, stop.Token));
    }

    private async Task<CollectorUploadResponse> SendChunkedUploadAsync(Uri endpoint,
        string compressedPath, string matchUid, string realm, string packageEncoding)
    {
        const int chunkBytes = 1024 * 1024;
        const int maximumParallelChunks = 2;
        var sourceLength = new FileInfo(compressedPath).Length;
        var chunkCount = checked((int)((sourceLength + chunkBytes - 1) / chunkBytes));
        if (chunkCount is < 1 or > 128)
            throw new InvalidDataException("Compressed upload requires an invalid number of chunks.");
        var resume = await TryGetUploadStatusAsync(matchUid, realm, packageEncoding);
        var received = resume?.ChunkCount == chunkCount
            ? new HashSet<int>(resume.Received ?? Array.Empty<int>())
            : new HashSet<int>();
        var indexes = resume?.Complete == true
            ? new[] { 0 }
            : Enumerable.Range(0, chunkCount).Where(index => !received.Contains(index)).ToArray();
        // All parts can exist after a process interruption just before assembly.
        if (indexes.Length == 0) indexes = new[] { chunkCount - 1 };
        CollectorUploadResponse? result = null;
        var timer = Stopwatch.StartNew();
        long completedBytes = received.Sum(index =>
            Math.Min((long)chunkBytes, sourceLength - (long)index * chunkBytes));
        var completedChunks = received.Count;
        using var slots = new SemaphoreSlim(maximumParallelChunks, maximumParallelChunks);
        async Task<CollectorUploadResponse> SendChunkAsync(int chunkIndex)
        {
            await slots.WaitAsync(stop.Token);
            try
            {
                var buffer = new byte[chunkBytes];
                await using var source = new FileStream(compressedPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, chunkBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
                source.Seek((long)chunkIndex * chunkBytes, SeekOrigin.Begin);
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(length, buffer.Length - length),
                        stop.Token);
                    if (read == 0) break;
                    length += read;
                }
                if (length == 0) throw new EndOfStreamException("Compressed upload ended early.");
                var response = await SendUploadChunkWithFallbackAsync(endpoint, buffer, length,
                    matchUid, realm, chunkIndex, chunkCount, packageEncoding);
                if (!response.Ok)
                    throw new InvalidDataException(response.Error ?? "Server rejected an upload chunk.");
                var bytes = Interlocked.Add(ref completedBytes, length);
                var chunks = Interlocked.Increment(ref completedChunks);
                uploadProgress[matchUid] = new UploadProgress("uploading", bytes,
                    sourceLength, chunks, chunkCount,
                    bytes / Math.Max(.001, timer.Elapsed.TotalSeconds), timer.Elapsed);
                return response;
            }
            finally { slots.Release(); }
        }
        var results = await Task.WhenAll(indexes.Select(SendChunkAsync));
        result = results.LastOrDefault(value =>
                     !string.Equals(value.Status, "uploading", StringComparison.OrdinalIgnoreCase))
                 ?? results.LastOrDefault();
        return result ?? throw new InvalidDataException("Server returned no upload result.");
    }

    private async Task<CollectorUploadStatusResponse?> TryGetUploadStatusAsync(
        string matchUid, string realm, string packageEncoding)
    {
        try
        {
            var endpoint = new Uri(configuration.ServerBaseUrl.TrimEnd('/') +
                                   "/api/collector/v2/matches");
            (HttpStatusCode statusCode, string body) response;
            try
            {
                response = await SendUploadStatusAttemptAsync(PrimaryClientFor(endpoint),
                    endpoint, matchUid, realm, packageEncoding);
            }
            catch (Exception ex) when (FallbackClientFor(endpoint) is { } fallback &&
                                       IsRetryableTransportFailure(ex))
            {
                log.Debug(ex, "Direct upload resume query failed; retrying via system proxy");
                response = await SendUploadStatusAttemptAsync(fallback, endpoint, matchUid, realm,
                    packageEncoding);
            }
            var (statusCode, body) = response;
            if ((int)statusCode is < 200 or >= 300)
            {
                log.Debug("Upload resume query unavailable: HTTP {Status}", (int)statusCode);
                return null;
            }
            var status = JsonSerializer.Deserialize<CollectorUploadStatusResponse>(body, JsonOptions);
            return status?.Ok == true ? status : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            // Resume discovery is an optimization. A failure falls back to
            // idempotently sending every chunk through the established API.
            log.Debug(ex, "Upload resume query failed for {MatchUid}", matchUid);
            return null;
        }
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendUploadStatusAttemptAsync(
        HttpClient client, Uri endpoint, string matchUid, string realm, string packageEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        PreferHttp2(request);
        AddAuthorization(request);
        request.Headers.Add("X-Battlefield-Realm", realm);
        request.Headers.Add("X-Match-Uid", matchUid);
        request.Headers.Add("X-Battlefield-Package-Encoding", packageEncoding);
        request.Headers.Add("X-Battlefield-Upload-Probe", "status");
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, stop.Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(stop.Token));
    }

    private async Task<CollectorUploadResponse> SendUploadChunkWithFallbackAsync(Uri endpoint,
        byte[] payload, int length, string matchUid, string realm, int chunkIndex, int chunkCount,
        string packageEncoding)
    {
        var primaryClient = PrimaryClientFor(endpoint);
        var fallbackClient = FallbackClientFor(endpoint);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return await SendUploadChunkAttemptAsync(primaryClient, endpoint, payload, length,
                    matchUid, realm, chunkIndex, chunkCount, packageEncoding);
            }
            catch (RateLimitException rateLimit) when (attempt < 4)
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(100, 450));
                await Task.Delay(rateLimit.RetryAfter + jitter, stop.Token);
            }
            catch (Exception ex) when (fallbackClient != null && IsRetryableTransportFailure(ex))
            {
                log.Warning(ex,
                    "Direct Cloudflare upload failed for chunk {Chunk}/{Count} of {MatchUid}; retrying via system proxy",
                    chunkIndex + 1, chunkCount, matchUid);
                try
                {
                    return await SendUploadChunkAttemptAsync(fallbackClient, endpoint, payload,
                        length, matchUid, realm, chunkIndex, chunkCount, packageEncoding);
                }
                catch (RateLimitException rateLimit) when (attempt < 4)
                {
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(100, 450));
                    await Task.Delay(rateLimit.RetryAfter + jitter, stop.Token);
                }
            }
        }
        throw new InvalidOperationException("Upload remained rate limited after five attempts.");
    }

    private async Task<CollectorUploadResponse> SendUploadChunkAttemptAsync(HttpClient client,
        Uri endpoint, byte[] payload, int length, string matchUid, string realm,
        int chunkIndex, int chunkCount, string packageEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        PreferHttp2(request);
        AddAuthorization(request);
        request.Headers.Add("X-Battlefield-Realm", realm);
        request.Headers.Add("X-Match-Uid", matchUid);
        request.Headers.Add("X-Battlefield-Package-Encoding", packageEncoding);
        request.Headers.Add("X-Battlefield-Chunk-Index", chunkIndex.ToString());
        request.Headers.Add("X-Battlefield-Chunk-Count", chunkCount.ToString());
        request.Content = new ByteArrayContent(payload, 0, length);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, stop.Token);
        var body = await response.Content.ReadAsStringAsync(stop.Token);
        if (response.StatusCode == (HttpStatusCode)429)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date is { } date
                    ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2));
            retryAfter = TimeSpan.FromSeconds(Math.Clamp(retryAfter.TotalSeconds, 1, 30));
            throw new RateLimitException(retryAfter, $"HTTP 429: {body}");
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<CollectorUploadResponse>(body, JsonOptions)
               ?? throw new InvalidDataException("Server returned an empty analysis response.");
    }

    private static bool IsRetryableTransportFailure(Exception exception)
    {
        if (exception is HttpRequestException or IOException) return true;
        return exception is InvalidOperationException &&
               (exception.Message.StartsWith("HTTP 502", StringComparison.Ordinal) ||
                exception.Message.StartsWith("HTTP 503", StringComparison.Ordinal) ||
                exception.Message.StartsWith("HTTP 504", StringComparison.Ordinal) ||
                exception.Message.StartsWith("HTTP 520", StringComparison.Ordinal) ||
                exception.Message.StartsWith("HTTP 522", StringComparison.Ordinal) ||
                exception.Message.StartsWith("HTTP 523", StringComparison.Ordinal) ||
                exception.Message.StartsWith("HTTP 524", StringComparison.Ordinal));
    }

    private static bool IsRetryableGatewayStatus(HttpStatusCode status)
        => (int)status is 502 or 503 or 504 or 520 or 522 or 523 or 524;

    private static async Task<string> CompressUploadAsync(string sourcePath,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.Combine(Path.GetTempPath(),
            $"battlefield-upload-{Guid.NewGuid():N}.jsonl.gz");
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var gzip = new GZipStream(destination, CompressionLevel.Fastest,
                             leaveOpen: false))
                await source.CopyToAsync(gzip, 64 * 1024, cancellationToken);
            return destinationPath;
        }
        catch
        {
            File.Delete(destinationPath);
            throw;
        }
    }

    private static string UploadErrorCode(Exception exception)
    {
        if (exception.Message == "missing_token") return "missing_token";
        if (exception.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("invalid token", StringComparison.OrdinalIgnoreCase)) return "invalid_token";
        if (exception is HttpRequestException) return "network_error";
        if (exception is TimeoutException) return "analysis_timeout";
        return exception.Message.Length <= 240 ? exception.Message : exception.Message[..240];
    }

    private async Task PollUntilReadyAsync(string matchUid, string pollUrl)
    {
        var url = pollUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pollUrl : configuration.ServerBaseUrl.TrimEnd('/') + "/" + pollUrl.TrimStart('/');
        var deadline = DateTime.UtcNow.AddMinutes(3);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay, stop.Token);
            var (statusCode, body) = await SendAuthorizedGetAsync(url);
            if ((int)statusCode is < 200 or >= 300)
                throw new InvalidOperationException($"Polling HTTP {(int)statusCode}: {body}");
            var result = JsonSerializer.Deserialize<CollectorUploadResponse>(body, JsonOptions)
                         ?? throw new InvalidDataException("Server returned an empty polling response.");
            if (result.Status == "ready")
            {
                repository.MergeRemote(matchUid, result);
                return;
            }
            if (result.Status == "failed")
                throw new InvalidDataException(result.Error ?? "Server analysis failed.");
            delay = Math.Min(2000, delay + 250);
        }
        throw new TimeoutException("Server analysis did not finish within three minutes.");
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        var token = CollectorToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Battlefield-Machine-Id", machineId);
            var currentGameAccountId = gameAccountId();
            if (!string.IsNullOrWhiteSpace(currentGameAccountId))
                request.Headers.TryAddWithoutValidation("X-Battlefield-Game-Account-Id", currentGameAccountId);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        wake.Release();
        try { worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        directClient.Dispose(); publicClient.Dispose(); wake.Dispose(); stop.Dispose();
    }
}
