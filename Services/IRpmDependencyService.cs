namespace abaci_bot.Services;

/// <summary>
/// Service abstraction for building reverse dependency graphs from RPM repository metadata (primary.xml.gz)
/// and evaluating downstream rebuild impact.
/// </summary>
public interface IRpmDependencyService
{
    /// <summary>
    /// Computes the set of downstream packages that depend on the specified package.
    /// Supports direct dependencies or transitive closure traversal with optional max depth.
    /// </summary>
    /// <param name="packageName">The source package being modified.</param>
    /// <param name="maxDepth">Optional maximum recursion depth for transitive closure.</param>
    /// <returns>A set of affected package names.</returns>
    IReadOnlySet<string> GetAffectedPackages(string packageName, int? maxDepth = null);

    /// <summary>
    /// Maps a count of affected downstream packages to the standard Rebuild:* label string.
    /// </summary>
    string MapAffectedCountToRebuildLabel(int affectedCount);

    /// <summary>
    /// Loads and parses repository primary metadata XML stream (optionally gzipped).
    /// </summary>
    Task LoadMetadataAsync(Stream stream, bool isGzipped = true, CancellationToken ct = default);

    /// <summary>
    /// Resolves the relative repository location href for a package (e.g. "Packages/libgpg-error-1.47-1.oe2403.x86_64.rpm")
    /// extracted from primary.xml metadata.
    /// </summary>
    string? GetPackageLocation(string packageName);

    /// <summary>
    /// Initializes or refreshes metadata from configured remote URL or local cache file.
    /// </summary>
    Task<bool> RefreshMetadataAsync(CancellationToken ct = default);
}
