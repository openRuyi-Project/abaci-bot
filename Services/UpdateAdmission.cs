using System.Text.RegularExpressions;
using abaci_bot.Options;

namespace abaci_bot.Services;

/// <summary>
/// Deep module unifying package update admission decisions, version comparison,
/// branch deduction for Rust crates, and spec invariants.
/// Pulls complexity down from individual services and prevents temporal decomposition.
/// </summary>
public static class UpdateAdmission
{
    private static readonly Regex FullVersionRegex = new(
        @"^%global\s+full_version\s+(?<ver>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex VersionRegex = new(
        @"^Version:\s*(?<ver>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Deduce branch name for a Rust crate package given the directory name and crate name.
    /// Handles underscore to hyphen normalization (e.g. rand_core -> rust-rand-core-0.10).
    /// Returns the deduced branch (e.g. "0.10") or empty string if no specific branch constraint.
    /// </summary>
    public static string DeduceBranch(string? dirName, string crateName)
    {
        if (string.IsNullOrWhiteSpace(dirName) || string.IsNullOrWhiteSpace(crateName))
            return string.Empty;

        var normalizedCrate = crateName.Trim().Replace('_', '-');
        var prefix = $"rust-{normalizedCrate}-";

        if (dirName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return dirName.Substring(prefix.Length);
        }

        return string.Empty;
    }

    /// <summary>
    /// Checks whether the target version is compatible with the branch name deduced from the directory.
    /// E.g. branch "0.6" requires target "0.6.x"; branch "1" requires target "1.x".
    /// </summary>
    public static bool IsVersionCompatibleWithBranch(string targetVersion, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            return true;

        if (string.Equals(targetVersion, branch, StringComparison.OrdinalIgnoreCase))
            return true;

        var branchPrefix = branch.EndsWith('.') ? branch : branch + ".";
        return targetVersion.StartsWith(branchPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips build metadata and vendor suffixes (+suffix) from a version string.
    /// E.g. "0.4.88+curl-8.19.0" -> "0.4.88".
    /// </summary>
    public static string StripVersionSuffix(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        var plusIdx = version.IndexOf('+');
        return plusIdx >= 0 ? version.Substring(0, plusIdx).Trim() : version.Trim();
    }

    /// <summary>
    /// Evaluates if the current version in a SPEC already satisfies or matches the target version,
    /// ignoring '+suffix' vendor metadata.
    /// </summary>
    public static bool IsVersionAtTarget(string specContent, string targetVersion)
    {
        if (string.IsNullOrWhiteSpace(specContent) || string.IsNullOrWhiteSpace(targetVersion))
            return false;

        var cleanTarget = StripVersionSuffix(targetVersion);

        // Check Version: tag
        var verMatch = VersionRegex.Match(specContent);
        if (verMatch.Success)
        {
            var cleanVer = StripVersionSuffix(verMatch.Groups["ver"].Value);
            if (string.Equals(cleanVer, cleanTarget, StringComparison.Ordinal))
                return true;
        }

        // Check %global full_version tag
        var fullVerMatch = FullVersionRegex.Match(specContent);
        if (fullVerMatch.Success)
        {
            var cleanFullVer = StripVersionSuffix(fullVerMatch.Groups["ver"].Value);
            if (string.Equals(cleanFullVer, cleanTarget, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether an upgrade crosses a major version boundary.
    /// Parses the leading numeric component before the first dot.
    /// </summary>
    public static bool IsCrossMajorVersion(string currentVersion, string targetVersion)
    {
        var cleanCurrent = StripVersionSuffix(currentVersion);
        var cleanTarget = StripVersionSuffix(targetVersion);

        if (string.IsNullOrWhiteSpace(cleanCurrent) || string.IsNullOrWhiteSpace(cleanTarget))
            return false;

        var currentMajor = ExtractMajorComponent(cleanCurrent);
        var targetMajor = ExtractMajorComponent(cleanTarget);

        if (currentMajor != null && targetMajor != null)
        {
            return currentMajor != targetMajor;
        }

        return false;
    }

    private static int? ExtractMajorComponent(string version)
    {
        var dotIdx = version.IndexOf('.');
        var majorStr = dotIdx >= 0 ? version.Substring(0, dotIdx) : version;

        // Trim leading non-digits if any (e.g. v1.2.3 -> 1)
        var span = majorStr.AsSpan().TrimStart("vV");
        if (int.TryParse(span, out var major))
            return major;

        return null;
    }
}

