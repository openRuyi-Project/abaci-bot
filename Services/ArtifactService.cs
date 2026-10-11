using System.IO.Compression;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Service for downloading and extracting workflow artifacts and baseline RPM packages.
/// </summary>
public class ArtifactService : IArtifactService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AbiComplianceOptions _options;
    private readonly ILogger<ArtifactService>? _logger;

    public ArtifactService(
        IHttpClientFactory httpClientFactory,
        IOptions<AbiComplianceOptions> options,
        ILogger<ArtifactService>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
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
            var client = _httpClientFactory.CreateClient("GitHubArtifactClient");
            var url = $"https://api.github.com/repos/{owner}/{repo}/actions/artifacts/{artifactId}/zip";

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Failed to download artifact {ArtifactId}. Status: {StatusCode}", artifactId, response.StatusCode);
                return extractedRpms;
            }

            await using var zipStream = await response.Content.ReadAsStreamAsync(ct);
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
            var url = $"{_options.StableRpmBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageName)}.rpm";

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
