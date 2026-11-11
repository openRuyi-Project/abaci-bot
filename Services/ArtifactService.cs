using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Service for downloading and extracting workflow artifacts and baseline RPM packages.
/// </summary>
public class ArtifactService : IArtifactService
{
    private readonly IGitHubService _gitHubService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AbiComplianceOptions _options;
    private readonly IRpmDependencyService? _rpmDependencyService;
    private readonly ILogger<ArtifactService>? _logger;

    public ArtifactService(
        IGitHubService gitHubService,
        IHttpClientFactory httpClientFactory,
        IOptions<AbiComplianceOptions> options,
        IRpmDependencyService? rpmDependencyService = null,
        ILogger<ArtifactService>? logger = null)
    {
        _gitHubService = gitHubService ?? throw new ArgumentNullException(nameof(gitHubService));
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _rpmDependencyService = rpmDependencyService;
        _logger = logger;
    }

    internal sealed class CumulativeArtifactLimits
    {
        public const int MaxEntryCount = 1000;
        public const long MaxSingleFileSize = 500L * 1024 * 1024; // 500 MB
        public const long MaxTotalUncompressedSize = 2L * 1024 * 1024 * 1024; // 2 GB

        public int ProcessedEntries { get; set; }
        public long TotalUncompressedBytes { get; set; }
    }

    public Task<IReadOnlyList<string>> DownloadAndExtractRpmArtifactAsync(
        string owner, string repo, long artifactId, string destinationDirectory, CancellationToken ct = default)
    {
        return DownloadAndExtractRpmArtifactAsync(owner, repo, artifactId, destinationDirectory, new CumulativeArtifactLimits(), ct);
    }

    internal async Task<IReadOnlyList<string>> DownloadAndExtractRpmArtifactAsync(
        string owner, string repo, long artifactId, string destinationDirectory, CumulativeArtifactLimits limits, CancellationToken ct = default)
    {
        var extractedRpms = new List<string>();
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        await using var zipStream = await _gitHubService.DownloadArtifactZipStreamAsync(owner, repo, artifactId, ct);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            limits.ProcessedEntries++;
            if (limits.ProcessedEntries > CumulativeArtifactLimits.MaxEntryCount)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "Cumulative zip archive entry count exceeded safety limit of {0}.", CumulativeArtifactLimits.MaxEntryCount));
            }

            if (entry.Length > CumulativeArtifactLimits.MaxSingleFileSize)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "Zip entry '{0}' size {1} bytes exceeded maximum single file size limit of {2} bytes.",
                    entry.FullName, entry.Length, CumulativeArtifactLimits.MaxSingleFileSize));
            }

            limits.TotalUncompressedBytes += entry.Length;
            if (limits.TotalUncompressedBytes > CumulativeArtifactLimits.MaxTotalUncompressedSize)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "Cumulative uncompressed artifact size exceeded safety limit of {0} bytes.", CumulativeArtifactLimits.MaxTotalUncompressedSize));
            }

            if (entry.FullName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
            {
                var cleanEntryName = Path.GetFileName(entry.FullName);
                var isSrc = cleanEntryName.EndsWith(".src.rpm", StringComparison.OrdinalIgnoreCase) ||
                            cleanEntryName.EndsWith(".nosrc.rpm", StringComparison.OrdinalIgnoreCase);
                var isDebugsource = cleanEntryName.Contains("-debugsource", StringComparison.OrdinalIgnoreCase);

                if (isSrc || isDebugsource)
                {
                    _logger?.LogDebug("Skipping source or debugsource RPM entry in artifact {ArtifactId}: {EntryName}", artifactId, entry.FullName);
                    continue;
                }

                var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                if (!targetPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    _logger?.LogWarning("Skipping zip entry with path traversal: {EntryName}", entry.FullName);
                    continue;
                }

                var parentDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                entry.ExtractToFile(targetPath, overwrite: true);
                extractedRpms.Add(targetPath);
            }
        }

        return extractedRpms;
    }

    public async Task<IReadOnlyList<string>> DownloadRunRpmArtifactsAsync(
        string owner, string repo, long runId, string destinationDirectory, CancellationToken ct = default)
    {
        var allRpms = new List<string>();
        IReadOnlyList<(long Id, string Name, long SizeInBytes)> artifacts;
        try
        {
            artifacts = await _gitHubService.ListWorkflowRunArtifactsAsync(owner, repo, runId, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Failed to list artifacts for workflow run {RunId} in {Owner}/{Repo}", runId, owner, repo);
            return allRpms;
        }

        if (artifacts.Count == 0)
        {
            _logger?.LogInformation("No workflow run artifacts found for run {RunId} in {Owner}/{Repo}", runId, owner, repo);
            return allRpms;
        }

        var limits = new CumulativeArtifactLimits();
        foreach (var (artifactId, _, _) in artifacts)
        {
            var rpms = await DownloadAndExtractRpmArtifactAsync(owner, repo, artifactId, destinationDirectory, limits, ct);
            allRpms.AddRange(rpms);
        }

        return allRpms;
    }

    /// <summary>
    /// Determines whether an RPM filename corresponds to a binary package eligible for ABI comparison.
    /// Excludes source packages (.src.rpm, .nosrc.rpm) and debug packages (-debuginfo, -debugsource).
    /// </summary>
    public static bool IsEligibleBinaryRpm(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return false;

        var cleanName = Path.GetFileName(filename);

        // Exclude source RPMs
        if (cleanName.EndsWith(".src.rpm", StringComparison.OrdinalIgnoreCase) ||
            cleanName.EndsWith(".nosrc.rpm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Exclude debuginfo and debugsource packages
        if (cleanName.Contains("-debuginfo", StringComparison.OrdinalIgnoreCase) ||
            cleanName.Contains("-debugsource", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return cleanName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the package name from an RPM file name following the standard RPM naming convention
    /// [NAME]-[VERSION]-[RELEASE].[ARCH].rpm.
    /// In RPM NEVRA conventions, version and release do not contain hyphens, while package names frequently do.
    /// </summary>
    public static string ExtractPackageName(string rpmFileName)
    {
        if (string.IsNullOrWhiteSpace(rpmFileName))
            return string.Empty;

        var fileName = Path.GetFileName(rpmFileName);
        if (fileName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }

        var parts = fileName.Split('-');
        if (parts.Length >= 3 && parts[^1].Length > 0 && char.IsDigit(parts[^1][0]) &&
            parts[^2].Length > 0 && char.IsDigit(parts[^2][0]))
        {
            // Standard NEVRA: the last two segments are version and release.arch (both start with digits)
            return string.Join('-', parts[..^2]);
        }

        if (parts.Length >= 2 && parts[^1].Length > 0 && char.IsDigit(parts[^1][0]))
        {
            // Package name and version without release (e.g. my-pkg-1.0)
            return string.Join('-', parts[..^1]);
        }

        return parts[0];
    }

    /// <summary>
    /// Locates the corresponding -debuginfo RPM package for a given binary package from a collection of extracted RPM paths.
    /// Returns null if no matching debuginfo package is present.
    /// </summary>
    public static string? FindMatchingDebuginfoRpm(string binaryRpmPath, IEnumerable<string> allExtractedRpms)
    {
        if (string.IsNullOrWhiteSpace(binaryRpmPath))
            return null;

        var binaryFileName = Path.GetFileName(binaryRpmPath);
        var pkgName = ExtractPackageName(binaryFileName);
        if (string.IsNullOrWhiteSpace(pkgName))
            return null;

        var expectedPrefix = $"{pkgName}-debuginfo-";
        var debugCandidates = new List<string>();

        foreach (var candidate in allExtractedRpms)
        {
            var candidateFileName = Path.GetFileName(candidate);
            if (!candidateFileName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
                continue;

            if (candidateFileName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            if (candidateFileName.Contains("-debuginfo-", StringComparison.OrdinalIgnoreCase))
            {
                debugCandidates.Add(candidate);
            }
        }

        // Subpackage matching: map e.g. pcre2-tools -> pcre2-debuginfo
        foreach (var candidate in debugCandidates)
        {
            var candidateFileName = Path.GetFileName(candidate);
            var candidatePkg = ExtractPackageName(candidateFileName);
            if (candidatePkg.EndsWith("-debuginfo", StringComparison.OrdinalIgnoreCase))
            {
                var sourcePkg = candidatePkg[..^"-debuginfo".Length];
                if (!string.IsNullOrEmpty(sourcePkg) &&
                    pkgName.StartsWith($"{sourcePkg}-", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads an optional baseline debuginfo RPM from the configured stable mirror or release storage.
    /// Returns null if not found or if debuginfo packages are not published in the baseline repository.
    /// </summary>
    public Task<string?> DownloadBaselineDebuginfoRpmAsync(string packageName, string destinationDirectory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return Task.FromResult<string?>(null);

        var debugPackageName = packageName.EndsWith("-debuginfo", StringComparison.OrdinalIgnoreCase)
            ? packageName
            : $"{packageName}-debuginfo";
        return DownloadBaselineRpmAsync(debugPackageName, destinationDirectory, ct);
    }

    public async Task<string?> DownloadBaselineRpmAsync(string packageName, string destinationDirectory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.StableRpmBaseUrl))
            return null;

        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        var safePackageName = Path.GetFileName(packageName);
        if (string.IsNullOrWhiteSpace(safePackageName))
            return null;

        var targetFile = Path.Combine(destinationRoot, $"{safePackageName}-baseline.rpm");

        try
        {
            var client = _httpClientFactory.CreateClient("StableRpmClient");
            if (_rpmDependencyService != null && !_rpmDependencyService.IsMetadataLoaded)
            {
                _logger?.LogWarning(
                    "Repository repodata metadata is not loaded; cannot resolve exact location href for baseline package '{PackageName}'. Attempting direct URL fallback.",
                    packageName);
            }

            var locationHref = _rpmDependencyService?.GetPackageLocation(packageName);
            string url;
            if (!string.IsNullOrWhiteSpace(locationHref))
            {
                url = $"{_options.StableRpmBaseUrl.TrimEnd('/')}/{locationHref.TrimStart('/')}";
            }
            else if (packageName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
            {
                url = $"{_options.StableRpmBaseUrl.TrimEnd('/')}/{packageName.TrimStart('/')}";
            }
            else
            {
                url = $"{_options.StableRpmBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageName)}.rpm";
            }

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var baseUrlEndsWithPackages = _options.StableRpmBaseUrl.TrimEnd('/').EndsWith("/Packages", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(locationHref) && !baseUrlEndsWithPackages)
                {
                    var filePart = safePackageName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase)
                        ? safePackageName
                        : $"{Uri.EscapeDataString(safePackageName)}.rpm";
                    var fallbackPackagesUrl = $"{_options.StableRpmBaseUrl.TrimEnd('/')}/Packages/{filePart}";
                    using var fallbackResponse = await client.GetAsync(fallbackPackagesUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (fallbackResponse.IsSuccessStatusCode)
                    {
                        await using var fallbackStream = File.Create(targetFile);
                        await fallbackResponse.Content.CopyToAsync(fallbackStream, ct);
                        return targetFile;
                    }
                }

                _logger?.LogDebug("Baseline RPM for {PackageName} not found at {Url}", packageName, url);
                return null;
            }

            await using var fileStream = File.Create(targetFile);
            await response.Content.CopyToAsync(fileStream, ct);
            return targetFile;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to download baseline RPM for {PackageName}", packageName);
            return null;
        }
    }
}
