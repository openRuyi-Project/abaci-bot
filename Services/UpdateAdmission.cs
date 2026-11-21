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

    private static readonly Regex SafePackageNameRegex = new(
        @"^[a-zA-Z0-9_\-+.]+$",
        RegexOptions.Compiled);

    private static readonly Regex SafeVersionRegex = new(
        @"^(?:\d+:)?[a-zA-Z0-9_.+~^]+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Validates whether a package name conforms to safe RPM package naming rules.
    /// Rejects path traversal (.. or /), control characters, spaces, and unexpected symbols.
    /// </summary>
    public static bool IsValidPackageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim();
        if (trimmed.Contains("..") || trimmed.Contains('/') || trimmed.Contains('\\'))
            return false;

        return SafePackageNameRegex.IsMatch(trimmed);
    }

    /// <summary>
    /// Validates whether a version string conforms to safe RPM version rules.
    /// Rejects newlines, carriage returns, spaces, slashes, hyphens, and unexpected symbols.
    /// </summary>
    public static bool IsValidTargetVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var trimmed = version.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains('-'))
            return false;

        return SafeVersionRegex.IsMatch(trimmed);
    }

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
    /// Evaluates if the current version in a SPEC already satisfies, matches, or exceeds the target version,
    /// ignoring '+suffix' vendor metadata.
    /// </summary>
    public static bool IsVersionAtOrPastTarget(string specContent, string targetVersion)
    {
        if (string.IsNullOrWhiteSpace(specContent) || string.IsNullOrWhiteSpace(targetVersion))
            return false;

        var cleanTarget = StripVersionSuffix(targetVersion);

        // Check Version: tag
        var verMatch = VersionRegex.Match(specContent);
        if (verMatch.Success)
        {
            var cleanVer = StripVersionSuffix(verMatch.Groups["ver"].Value);
            if (CompareRpmVersions(cleanVer, cleanTarget) >= 0)
                return true;
        }

        // Check %global full_version tag
        var fullVerMatch = FullVersionRegex.Match(specContent);
        if (fullVerMatch.Success)
        {
            var cleanFullVer = StripVersionSuffix(fullVerMatch.Groups["ver"].Value);
            if (CompareRpmVersions(cleanFullVer, cleanTarget) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates if the current version in a SPEC already satisfies or matches the target version,
    /// ignoring '+suffix' vendor metadata. Kept for backwards compatibility.
    /// </summary>
    public static bool IsVersionAtTarget(string specContent, string targetVersion)
        => IsVersionAtOrPastTarget(specContent, targetVersion);

    /// <summary>
    /// Compares two RPM version strings according to standard rpmvercmp rules.
    /// Handles digits, alpha segments, '~' (tilde sorts before anything), and '^' (caret sorts after base version).
    /// Returns 1 if v1 > v2, -1 if v1 &lt; v2, and 0 if equal.
    /// </summary>
    public static int CompareRpmVersions(string? v1, string? v2)
    {
        var (epoch1, rawVer1) = SplitEpoch(StripVersionSuffix(v1 ?? string.Empty));
        var (epoch2, rawVer2) = SplitEpoch(StripVersionSuffix(v2 ?? string.Empty));

        var epochCmp = epoch1.CompareTo(epoch2);
        if (epochCmp != 0)
            return epochCmp > 0 ? 1 : -1;

        var str1 = rawVer1;
        var str2 = rawVer2;

        if (string.Equals(str1, str2, StringComparison.Ordinal))
            return 0;

        int i1 = 0, i2 = 0;
        while (i1 < str1.Length || i2 < str2.Length)
        {
            // Skip non-alphanumeric, non-tilde, non-caret separators (e.g. '.', '_', '-')
            while (i1 < str1.Length && !char.IsLetterOrDigit(str1[i1]) && str1[i1] != '~' && str1[i1] != '^')
                i1++;
            while (i2 < str2.Length && !char.IsLetterOrDigit(str2[i2]) && str2[i2] != '~' && str2[i2] != '^')
                i2++;

            // Handle ~ (tilde)
            if ((i1 < str1.Length && str1[i1] == '~') || (i2 < str2.Length && str2[i2] == '~'))
            {
                if (i1 >= str1.Length || str1[i1] != '~') return 1; // v2 has ~, v1 does not -> v1 is newer
                if (i2 >= str2.Length || str2[i2] != '~') return -1; // v1 has ~, v2 does not -> v2 is newer
                i1++; i2++;
                continue;
            }

            // Handle ^ (caret)
            if ((i1 < str1.Length && str1[i1] == '^') || (i2 < str2.Length && str2[i2] == '^'))
            {
                if (i1 >= str1.Length) return -1; // v1 ended, v2 has ^ -> v2 is newer
                if (i2 >= str2.Length) return 1;  // v2 ended, v1 has ^ -> v1 is newer
                if (str1[i1] == '^' && str2[i2] != '^') return -1;
                if (str1[i1] != '^' && str2[i2] == '^') return 1;
                i1++; i2++;
                continue;
            }

            if (i1 >= str1.Length) return i2 < str2.Length ? -1 : 0;
            if (i2 >= str2.Length) return 1;

            bool isDigit1 = char.IsDigit(str1[i1]);
            bool isDigit2 = char.IsDigit(str2[i2]);

            // Number segment vs letter segment
            if (isDigit1 && !isDigit2) return 1;
            if (!isDigit1 && isDigit2) return -1;

            if (isDigit1)
            {
                int start1 = i1;
                while (i1 < str1.Length && char.IsDigit(str1[i1])) i1++;
                int start2 = i2;
                while (i2 < str2.Length && char.IsDigit(str2[i2])) i2++;

                var seg1 = str1.Substring(start1, i1 - start1).TrimStart('0');
                var seg2 = str2.Substring(start2, i2 - start2).TrimStart('0');

                if (seg1.Length > seg2.Length) return 1;
                if (seg1.Length < seg2.Length) return -1;
                int cmp = string.CompareOrdinal(seg1, seg2);
                if (cmp != 0) return cmp > 0 ? 1 : -1;
            }
            else
            {
                int start1 = i1;
                while (i1 < str1.Length && char.IsLetter(str1[i1])) i1++;
                int start2 = i2;
                while (i2 < str2.Length && char.IsLetter(str2[i2])) i2++;

                var seg1 = str1.Substring(start1, i1 - start1);
                var seg2 = str2.Substring(start2, i2 - start2);

                int cmp = string.CompareOrdinal(seg1, seg2);
                if (cmp != 0) return cmp > 0 ? 1 : -1;
            }
        }

        return 0;
    }

    private static (long Epoch, string Version) SplitEpoch(string input)
    {
        var colonIdx = input.IndexOf(':');
        if (colonIdx > 0 && long.TryParse(input[..colonIdx], out var epoch) && epoch >= 0)
        {
            return (epoch, input[(colonIdx + 1)..]);
        }

        return (0, input);
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
        {
            // CalVer support:
            // 8-digit date (YYYYMMDD, e.g. 20260526 -> 2026)
            if (major >= 19900000 && major <= 21000000)
            {
                return major / 10000;
            }

            // 6-digit date (YYYYMM, e.g. 202602, 202608 -> 2026)
            if (major >= 199000 && major <= 210012)
            {
                return major / 100;
            }

            return major;
        }

        return null;
    }
}

