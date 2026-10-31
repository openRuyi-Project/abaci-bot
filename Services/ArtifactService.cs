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

    public async Task<IReadOnlyList<string>> DownloadAndExtractRpmArtifactAsync(
        string owner, string repo, long artifactId, string destinationDirectory, CancellationToken ct = default)
    {
        var extractedRpms = new List<string>();
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        try
        {
            await using var zipStream = await _gitHubService.DownloadArtifactZipStreamAsync(owner, repo, artifactId, ct);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
                {
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Error occurred while extracting artifact {ArtifactId}", artifactId);
        }

        return extractedRpms;
    }

    public async Task<IReadOnlyList<string>> DownloadRunRpmArtifactsAsync(
        string owner, string repo, long runId, string destinationDirectory, CancellationToken ct = default)
    {
        var allRpms = new List<string>();
        try
        {
            var artifacts = await _gitHubService.ListWorkflowRunArtifactsAsync(owner, repo, runId, ct);
            if (artifacts.Count == 0)
            {
                _logger?.LogInformation("No workflow run artifacts found for run {RunId} in {Owner}/{Repo}", runId, owner, repo);
                return allRpms;
            }

            foreach (var (artifactId, _, _) in artifacts)
            {
                var rpms = await DownloadAndExtractRpmArtifactAsync(owner, repo, artifactId, destinationDirectory, ct);
                allRpms.AddRange(rpms);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to fetch artifacts for workflow run {RunId}", runId);
        }

        return allRpms;
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
