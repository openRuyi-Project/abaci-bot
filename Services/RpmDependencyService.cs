using System.IO.Compression;
using System.Xml;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Deep module implementing streaming RPM primary.xml.gz parsing,
/// in-memory reverse dependency DAG construction, and rebuild impact calculation.
/// </summary>
public class RpmDependencyService : IRpmDependencyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RebuildImpactOptions _options;
    private readonly ILogger<RpmDependencyService>? _logger;

    // Key: provider package name, Value: set of downstream packages directly requiring it
    private Dictionary<string, HashSet<string>> _reverseGraph = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _packageLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _graphLock = new();

    public RpmDependencyService(
        IHttpClientFactory httpClientFactory,
        IOptions<RebuildImpactOptions> options,
        ILogger<RpmDependencyService>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public string? GetPackageLocation(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return null;

        lock (_graphLock)
        {
            return _packageLocations.TryGetValue(packageName, out var loc) ? loc : null;
        }
    }

    public IReadOnlySet<string> GetAffectedPackages(string packageName, int? maxDepth = null)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return new HashSet<string>();

        Dictionary<string, HashSet<string>> graphSnapshot;
        lock (_graphLock)
        {
            graphSnapshot = _reverseGraph;
        }

        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!graphSnapshot.TryGetValue(packageName, out var directDependents) || directDependents.Count == 0)
        {
            return affected;
        }

        var isDirectOnly = string.Equals(_options.CalculationMode, "Direct", StringComparison.OrdinalIgnoreCase) ||
                           (maxDepth.HasValue && maxDepth.Value <= 1);

        if (isDirectOnly)
        {
            foreach (var dep in directDependents)
            {
                if (!string.Equals(dep, packageName, StringComparison.OrdinalIgnoreCase))
                    affected.Add(dep);
            }
            return affected;
        }

        // Transitive BFS traversal
        var effectiveMaxDepth = maxDepth ?? _options.MaxDepth;
        var queue = new Queue<(string Package, int Depth)>();

        foreach (var dep in directDependents)
        {
            if (!string.Equals(dep, packageName, StringComparison.OrdinalIgnoreCase) && affected.Add(dep))
            {
                queue.Enqueue((dep, 1));
            }
        }

        while (queue.Count > 0)
        {
            var (currentPkg, currentDepth) = queue.Dequeue();

            if (effectiveMaxDepth.HasValue && currentDepth >= effectiveMaxDepth.Value)
                continue;

            if (graphSnapshot.TryGetValue(currentPkg, out var nextDependents))
            {
                foreach (var nextDep in nextDependents)
                {
                    if (!string.Equals(nextDep, packageName, StringComparison.OrdinalIgnoreCase) && affected.Add(nextDep))
                    {
                        queue.Enqueue((nextDep, currentDepth + 1));
                    }
                }
            }
        }

        return affected;
    }

    public string MapAffectedCountToRebuildLabel(int affectedCount)
    {
        if (affectedCount <= 0) return GitHubLabels.Rebuild0;
        if (affectedCount <= 10) return GitHubLabels.Rebuild1To10;
        if (affectedCount <= 100) return GitHubLabels.Rebuild11To100;
        if (affectedCount <= 500) return GitHubLabels.Rebuild101To500;
        if (affectedCount <= 1000) return GitHubLabels.Rebuild501To1000;
        if (affectedCount <= 2500) return GitHubLabels.Rebuild1001To2500;
        if (affectedCount <= 5000) return GitHubLabels.Rebuild2501To5000;
        return GitHubLabels.Rebuild5001Plus;
    }

    public async Task LoadMetadataAsync(Stream stream, bool isGzipped = true, CancellationToken ct = default)
    {
        var header = new byte[4];
        var bytesRead = await stream.ReadAtLeastAsync(header.AsMemory(0, 4), 4, throwOnEndOfStream: false, ct);

        Stream baseStream;
        if (stream.CanSeek)
        {
            stream.Position = 0;
            baseStream = stream;
        }
        else
        {
            var prefixStream = new MemoryStream(header, 0, bytesRead);
            baseStream = new ReplayableStream(prefixStream, stream);
        }

        Stream decompressionStream = baseStream;
        IDisposable? disposableStream = null;

        // Magic 0x28B52FFD: Zstandard (.zst)
        if (bytesRead >= 4 && header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD)
        {
            var zstd = new ZstdSharp.DecompressionStream(baseStream);
            disposableStream = zstd;
            decompressionStream = zstd;
        }
        // Magic 0x1F8B: Gzip (.gz)
        else if (bytesRead >= 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            var gzip = new GZipStream(baseStream, CompressionMode.Decompress, leaveOpen: true);
            disposableStream = gzip;
            decompressionStream = gzip;
        }
        // Explicit isGzipped flag fallback if non-seekable and not matching zstd
        else if (isGzipped && bytesRead > 0 && header[0] != '<')
        {
            var gzip = new GZipStream(baseStream, CompressionMode.Decompress, leaveOpen: true);
            disposableStream = gzip;
            decompressionStream = gzip;
        }

        try
        {
            await Task.Run(() => ParseXmlStream(decompressionStream, ct), ct);
        }
        finally
        {
            disposableStream?.Dispose();
        }
    }

    private sealed class ReplayableStream : Stream
    {
        private readonly Stream _prefix;
        private readonly Stream _tail;

        public ReplayableStream(Stream prefix, Stream tail)
        {
            _prefix = prefix;
            _tail = tail;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _prefix.Read(buffer, offset, count);
            if (read > 0) return read;
            return _tail.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _prefix.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;
            return await _tail.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
                _tail.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private void ParseXmlStream(Stream stream, CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
            Async = false
        };

        using var reader = XmlReader.Create(stream, settings);

        // Map capability -> set of providing package names
        var capabilityProviders = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // Map package name -> set of required capabilities
        var packageRequirements = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var packageLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? currentPackage = null;
        bool inProvides = false;
        bool inRequires = false;

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element)
            {
                var name = reader.LocalName;

                if (name == "package" && reader.GetAttribute("type") == "rpm")
                {
                    currentPackage = null;
                }
                else if (name == "name" && currentPackage == null && reader.Depth <= 3)
                {
                    currentPackage = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(currentPackage))
                    {
                        // A package always provides its own name
                        if (!capabilityProviders.TryGetValue(currentPackage, out var list))
                        {
                            list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            capabilityProviders[currentPackage] = list;
                        }
                        list.Add(currentPackage);

                        if (!packageRequirements.ContainsKey(currentPackage))
                        {
                            packageRequirements[currentPackage] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
                else if (name == "location" && currentPackage != null)
                {
                    var href = reader.GetAttribute("href");
                    if (!string.IsNullOrWhiteSpace(href))
                    {
                        packageLocations[currentPackage] = href;
                    }
                }
                else if (name == "provides")
                {
                    inProvides = true;
                }
                else if (name == "requires")
                {
                    inRequires = true;
                }
                else if (name == "entry" && currentPackage != null)
                {
                    var entryName = reader.GetAttribute("name");
                    if (!string.IsNullOrWhiteSpace(entryName))
                    {
                        if (inProvides)
                        {
                            // Skip file paths and runtime internal capabilities to prevent DAG pollution
                            if (!entryName.StartsWith('/') &&
                                !entryName.StartsWith("rpmlib(", StringComparison.OrdinalIgnoreCase) &&
                                !entryName.StartsWith("rtld(", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!capabilityProviders.TryGetValue(entryName, out var provSet))
                                {
                                    provSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    capabilityProviders[entryName] = provSet;
                                }
                                provSet.Add(currentPackage);
                            }
                        }
                        else if (inRequires)
                        {
                            // Skip file paths (e.g. /bin/sh) and rpm/rtld internal requirements
                            // Scriptlet paths are runtime interpreter dependencies, not build/rebuild dependencies
                            if (!entryName.StartsWith('/') &&
                                !entryName.StartsWith("rpmlib(", StringComparison.OrdinalIgnoreCase) &&
                                !entryName.StartsWith("rtld(", StringComparison.OrdinalIgnoreCase))
                            {
                                if (packageRequirements.TryGetValue(currentPackage, out var reqSet))
                                {
                                    reqSet.Add(entryName);
                                }
                            }
                        }
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "provides")
                    inProvides = false;
                else if (reader.LocalName == "requires")
                    inRequires = false;
            }
        }

        // Build reverse dependency graph: provider package -> dependents
        var newReverseGraph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (consumerPkg, reqs) in packageRequirements)
        {
            foreach (var req in reqs)
            {
                if (capabilityProviders.TryGetValue(req, out var providers))
                {
                    foreach (var providerPkg in providers)
                    {
                        if (string.Equals(providerPkg, consumerPkg, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!newReverseGraph.TryGetValue(providerPkg, out var dependents))
                        {
                            dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            newReverseGraph[providerPkg] = dependents;
                        }
                        dependents.Add(consumerPkg);
                    }
                }
            }
        }

        lock (_graphLock)
        {
            _reverseGraph = newReverseGraph;
            _packageLocations = packageLocations;
        }

        _logger?.LogInformation(
            "Constructed RPM reverse dependency graph with {PackageCount} packages and {EdgeCount} dependency connections.",
            packageRequirements.Count, newReverseGraph.Values.Sum(v => v.Count));
    }

    public static string? ExtractPrimaryLocationFromRepomd(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
            Async = false
        };

        using var reader = XmlReader.Create(stream, settings);
        bool inPrimaryData = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "data" && reader.GetAttribute("type") == "primary")
                {
                    inPrimaryData = true;
                }
                else if (inPrimaryData && reader.LocalName == "location")
                {
                    var href = reader.GetAttribute("href");
                    if (!string.IsNullOrWhiteSpace(href))
                    {
                        return href;
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "data")
            {
                inPrimaryData = false;
            }
        }

        return null;
    }

    internal async Task<string?> ResolveEffectivePrimaryUrlAsync(HttpClient client, string configuredUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl))
            return null;

        // If explicitly pointing to repomd.xml
        if (configuredUrl.EndsWith("repomd.xml", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchPrimaryHrefFromRepomdAsync(client, configuredUrl, ct);
        }

        // If configured directly with a primary file or other URL, probe it first
        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, configuredUrl);
            var headResp = await client.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (headResp.IsSuccessStatusCode)
            {
                return configuredUrl;
            }

            // If 404 or not found, try to locate repomd.xml in the repodata directory to resolve dynamic content hash
            if (headResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var derivedRepomdUrl = TryDeriveRepomdUrl(configuredUrl);
                if (derivedRepomdUrl != null)
                {
                    _logger?.LogInformation("Configured PrimaryXmlUrl {Url} returned 404; falling back to repomd.xml at {RepomdUrl}",
                        configuredUrl, derivedRepomdUrl);
                    var primaryFromRepomd = await FetchPrimaryHrefFromRepomdAsync(client, derivedRepomdUrl, ct);
                    if (primaryFromRepomd != null)
                        return primaryFromRepomd;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Probing configured PrimaryXmlUrl {Url} failed, falling back to direct URL attempt", configuredUrl);
        }

        return configuredUrl;
    }

    private async Task<string?> FetchPrimaryHrefFromRepomdAsync(HttpClient client, string repomdUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync(repomdUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var locationHref = ExtractPrimaryLocationFromRepomd(stream);
            if (string.IsNullOrWhiteSpace(locationHref))
                return null;

            if (Uri.TryCreate(locationHref, UriKind.Absolute, out var absUri))
                return absUri.AbsoluteUri;

            var repomdUri = new Uri(repomdUrl);
            var repomdStr = repomdUri.AbsoluteUri;
            if (repomdStr.EndsWith("/repodata/repomd.xml", StringComparison.OrdinalIgnoreCase))
            {
                var baseUri = new Uri(repomdStr[..^"repodata/repomd.xml".Length]);
                return new Uri(baseUri, locationHref).AbsoluteUri;
            }
            if (repomdStr.EndsWith("repomd.xml", StringComparison.OrdinalIgnoreCase))
            {
                var baseUri = new Uri(repomdStr[..^"repomd.xml".Length]);
                return new Uri(baseUri, locationHref).AbsoluteUri;
            }

            return new Uri(repomdUri, locationHref).AbsoluteUri;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to resolve repomd.xml from {Url}", repomdUrl);
            return null;
        }
    }

    private static string? TryDeriveRepomdUrl(string url)
    {
        var repodataIdx = url.IndexOf("/repodata/", StringComparison.OrdinalIgnoreCase);
        if (repodataIdx >= 0)
        {
            var prefix = url[..(repodataIdx + "/repodata/".Length)];
            return prefix + "repomd.xml";
        }
        return null;
    }

    public async Task<bool> RefreshMetadataAsync(CancellationToken ct = default)
    {
        // 1. Try local cache file if configured and exists
        if (!string.IsNullOrWhiteSpace(_options.LocalCachePath) && File.Exists(_options.LocalCachePath))
        {
            try
            {
                var isGzip = _options.LocalCachePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
                await using var stream = File.OpenRead(_options.LocalCachePath);
                await LoadMetadataAsync(stream, isGzip, ct);
                _logger?.LogInformation("Loaded RPM metadata from local file: {Path}", _options.LocalCachePath);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to read local RPM metadata from {Path}", _options.LocalCachePath);
            }
        }

        // 2. Try remote URL download if configured
        if (!string.IsNullOrWhiteSpace(_options.PrimaryXmlUrl))
        {
            try
            {
                var client = _httpClientFactory.CreateClient("RpmMetadataClient");
                var effectiveUrl = await ResolveEffectivePrimaryUrlAsync(client, _options.PrimaryXmlUrl, ct);
                if (string.IsNullOrWhiteSpace(effectiveUrl))
                {
                    _logger?.LogWarning("Unable to resolve primary XML URL from configured PrimaryXmlUrl: {Url}", _options.PrimaryXmlUrl);
                    return false;
                }

                var response = await client.GetAsync(effectiveUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    var isGzip = effectiveUrl.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
                    await LoadMetadataAsync(stream, isGzip, ct);
                    _logger?.LogInformation("Loaded RPM metadata from remote URL: {Url}", effectiveUrl);
                    return true;
                }

                _logger?.LogWarning("Failed to download RPM metadata from {Url}: HTTP {StatusCode}", effectiveUrl, response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to fetch RPM metadata from remote URL: {Url}", _options.PrimaryXmlUrl);
            }
        }

        return false;
    }
}
