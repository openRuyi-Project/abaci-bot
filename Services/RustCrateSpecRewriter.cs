using System.Text.RegularExpressions;

namespace abaci_bot.Services;

/// <summary>
/// Specialized SPEC version rewriter for Rust crate RPM packages.
/// Deduce branch constraints by stripping "rust-{crate_name}-" prefix from directory names.
/// Synchronizes %global full_version and Version: tags while preserving column alignment.
/// Rejects pre-release target versions to maintain valid RPM NEVRAs.
/// </summary>
public class RustCrateSpecRewriter : ISpecVersionRewriter
{
    private static readonly Regex CrateNameRegex = new(
        @"^%global\s+crate_name\s+(?<name>\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FullVersionRegex = new(
        @"^(?<prefix>%global\s+full_version\s+)(?<ver>\S+)(?<suffix>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex VersionLineRegex = new(
        @"^(?<prefix>Version:\s*)(?<ver>\S+)(?<suffix>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public bool CanHandle(SpecContext context)
    {
        if (string.IsNullOrWhiteSpace(context?.Content))
            return false;

        return CrateNameRegex.IsMatch(context.Content);
    }

    public SpecRewriteResult Rewrite(SpecContext context, string targetVersion)
    {
        if (context == null || string.IsNullOrWhiteSpace(context.Content))
            return new SpecRewriteResult(false, string.Empty, "SPEC content is empty");

        if (string.IsNullOrWhiteSpace(targetVersion))
            return new SpecRewriteResult(false, context.Content, "Target version cannot be empty");

        // RPM Version tag cannot contain hyphens
        if (targetVersion.Contains('-'))
            return new SpecRewriteResult(false, context.Content, $"Target version '{targetVersion}' contains hyphen (pre-release versions not permitted in RPM Version tag)");

        var crateMatch = CrateNameRegex.Match(context.Content);
        if (!crateMatch.Success)
            return new SpecRewriteResult(false, context.Content, "No '%global crate_name' directive found in SPEC");

        var crateName = crateMatch.Groups["name"].Value.Trim();

        // Deduce branch from directory name using unified UpdateAdmission helper
        var dirName = context.DirectoryName?.Trim() ?? string.Empty;
        var branch = UpdateAdmission.DeduceBranch(dirName, crateName);
        if (!string.IsNullOrEmpty(branch))
        {
            if (!UpdateAdmission.IsVersionCompatibleWithBranch(targetVersion, branch))
            {
                return new SpecRewriteResult(false, context.Content,
                    $"Target version '{targetVersion}' does not satisfy branch constraint '{branch}' declared in directory '{dirName}'");
            }
        }

        var fullVerMatch = FullVersionRegex.Match(context.Content);
        var verMatch = VersionLineRegex.Match(context.Content);

        if (!verMatch.Success)
            return new SpecRewriteResult(false, context.Content, "No 'Version:' tag found in SPEC");

        // Check if both already match targetVersion
        bool verAlreadyTarget = string.Equals(verMatch.Groups["ver"].Value, targetVersion, StringComparison.Ordinal);
        bool fullVerAlreadyTarget = !fullVerMatch.Success || string.Equals(fullVerMatch.Groups["ver"].Value, targetVersion, StringComparison.Ordinal);

        if (verAlreadyTarget && fullVerAlreadyTarget)
        {
            return new SpecRewriteResult(true, context.Content, null, HasChanged: false);
        }

        // Token replacements
        // To avoid index shifts, perform replacements from right to left (higher index first)
        var replacements = new List<(int Index, int Length, string Replacement)>();

        var verGroup = verMatch.Groups["ver"];
        replacements.Add((verGroup.Index, verGroup.Length, targetVersion));

        if (fullVerMatch.Success)
        {
            var fullVerVal = fullVerMatch.Groups["ver"].Value;
            var plusIdx = fullVerVal.IndexOf('+');
            var replacementFullVer = targetVersion;
            if (plusIdx >= 0)
            {
                // Preserve +suffix (e.g. 0.4.87+curl-8.19.0 -> 0.4.88+curl-8.19.0)
                replacementFullVer = string.Concat(targetVersion, fullVerVal.AsSpan(plusIdx));
            }

            var fullVerGroup = fullVerMatch.Groups["ver"];
            replacements.Add((fullVerGroup.Index, fullVerGroup.Length, replacementFullVer));
        }

        // Sort descending by index
        replacements.Sort((a, b) => b.Index.CompareTo(a.Index));

        var updated = context.Content;
        foreach (var r in replacements)
        {
            updated = string.Concat(
                updated.AsSpan(0, r.Index),
                r.Replacement,
                updated.AsSpan(r.Index + r.Length));
        }

        var dir = !string.IsNullOrWhiteSpace(context.DirectoryName) ? context.DirectoryName : context.PackageName;
        return new SpecRewriteResult(true, updated, null, HasChanged: true, SpecPath: $"SPECS/{dir}/{context.PackageName}.spec");
    }

    /// <summary>
    /// Checks whether the target version is compatible with the branch name deduced from the directory.
    /// Delegated to UpdateAdmission for centralized invariant maintenance.
    /// </summary>
    public static bool IsVersionCompatibleWithBranch(string targetVersion, string branch)
    {
        return UpdateAdmission.IsVersionCompatibleWithBranch(targetVersion, branch);
    }
}

