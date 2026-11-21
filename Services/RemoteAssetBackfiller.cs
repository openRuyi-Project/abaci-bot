using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace abaci_bot.Services;

/// <summary>
/// Default implementation of <see cref="IRemoteAssetBackfiller"/>.
/// Hides the complexity of GitHub artifact APIs, zip decompression, diff parsing, and spec mutations.
/// </summary>
public class RemoteAssetBackfiller : IRemoteAssetBackfiller
{
    private readonly IGitHubService _gitHubService;
    private readonly ILogger<RemoteAssetBackfiller>? _logger;

    public RemoteAssetBackfiller(
        IGitHubService gitHubService,
        ILogger<RemoteAssetBackfiller>? logger = null)
    {
        _gitHubService = gitHubService ?? throw new ArgumentNullException(nameof(gitHubService));
        _logger = logger;
    }

    public async Task<bool> TryBackfillAsync(
        string owner,
        string repo,
        string branchName,
        int prNumber,
        long runId,
        CancellationToken ct = default)
    {
        try
        {
            var artifacts = await _gitHubService.ListWorkflowArtifactsAsync(owner, repo, runId, ct);
            var diffArtifact = artifacts.FirstOrDefault(a => a.Name != null && a.Name.Contains("remoteasset", StringComparison.OrdinalIgnoreCase));
            if (diffArtifact == null)
                return false;

            _logger?.LogInformation("Found remoteasset artifact '{Name}' (ID: {Id}) for PR #{PrNumber}; downloading diff to backfill",
                diffArtifact.Name, diffArtifact.Id, prNumber);

            await using var stream = await _gitHubService.DownloadArtifactAsync(owner, repo, diffArtifact.Id, ct);
            var diffContent = await ArtifactDiffExtractor.ExtractDiffContentAsync(stream, ct);
            if (string.IsNullOrWhiteSpace(diffContent))
                return false;

            bool backfilledAny = false;

            // Extract file path from diff header and process each modified spec file safely
            var fileMatches = Regex.Matches(diffContent, @"\+\+\+\s+b/(?<path>SPECS/\S+)", RegexOptions.Multiline);
            foreach (Match fileMatch in fileMatches)
            {
                var specPath = fileMatch.Groups["path"].Value.Trim();
                var changes = ArtifactDiffExtractor.ExtractFileRemoteAssetChanges(diffContent, specPath);
                if (changes.Count == 0)
                    continue;

                string currentSpec;
                try
                {
                    currentSpec = await _gitHubService.GetFileContentAsync(owner, repo, specPath, branchName, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Failed to load current spec {Path} from branch {Branch}", specPath, branchName);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(currentSpec))
                    continue;

                var updatedSpec = currentSpec;
                foreach (var (oldLine, newLine) in changes)
                {
                    if (updatedSpec.Contains(oldLine, StringComparison.Ordinal))
                    {
                        updatedSpec = updatedSpec.Replace(oldLine, newLine);
                    }
                }

                if (!string.Equals(updatedSpec, currentSpec, StringComparison.Ordinal))
                {
                    _logger?.LogInformation("Backfilling updated #!RemoteAsset checksums to branch {Branch} for PR #{PrNumber}", branchName, prNumber);
                    await _gitHubService.UpdateFileContentAsync(
                        owner, repo, specPath, "chore: update #!RemoteAsset sha256 checksums", updatedSpec, branchName, ct);
                    backfilledAny = true;
                }
            }

            return backfilledAny;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to backfill remoteasset diff artifact for PR #{PrNumber}", prNumber);
            return false;
        }
    }
}
