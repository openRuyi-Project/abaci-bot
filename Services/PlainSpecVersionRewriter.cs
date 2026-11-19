using System.Text.RegularExpressions;

namespace abaci_bot.Services;

/// <summary>
/// General-purpose pure-token SPEC version rewriter.
/// Replaces exclusively the captured version token, preserving leading whitespace/tabs and trailing comments.
/// Rejects pre-release versions containing hyphens to prevent invalid RPM NEVRAs.
/// </summary>
public class PlainSpecVersionRewriter : ISpecVersionRewriter
{
    private static readonly Regex VersionLineRegex = new(
        @"^(?<prefix>Version:\s*)(?<ver>\S+)(?<suffix>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex RustCrateMarker = new(
        @"^%global\s+crate_name\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public bool CanHandle(SpecContext context)
    {
        if (string.IsNullOrWhiteSpace(context?.Content))
            return false;

        // If it defines %global crate_name, delegate to RustCrateSpecRewriter
        if (RustCrateMarker.IsMatch(context.Content))
            return false;

        var match = VersionLineRegex.Match(context.Content);
        if (!match.Success)
            return false;

        // Reject macro versions (e.g. Version: %{base_version})
        var verToken = match.Groups["ver"].Value;
        if (verToken.Contains("%{"))
            return false;

        return true;
    }

    public SpecRewriteResult Rewrite(SpecContext context, string targetVersion)
    {
        if (context == null || string.IsNullOrWhiteSpace(context.Content))
            return new SpecRewriteResult(false, string.Empty, "SPEC content is empty");

        if (string.IsNullOrWhiteSpace(targetVersion))
            return new SpecRewriteResult(false, context.Content, "Target version cannot be empty");

        // RPM Version tag cannot contain hyphens (- is reserved as delimiter between Version and Release)
        if (targetVersion.Contains('-'))
            return new SpecRewriteResult(false, context.Content, $"Target version '{targetVersion}' contains hyphen (pre-release versions not permitted in RPM Version tag)");

        var match = VersionLineRegex.Match(context.Content);
        if (!match.Success)
            return new SpecRewriteResult(false, context.Content, "No 'Version:' tag found in SPEC content");

        var currentVersion = match.Groups["ver"].Value;
        if (string.Equals(currentVersion, targetVersion, StringComparison.Ordinal))
        {
            // Empty update guard: version is already identical
            return new SpecRewriteResult(true, context.Content, null, HasChanged: false);
        }

        // Token-only replacement: only replace the exact character range of group "ver"
        var verGroup = match.Groups["ver"];
        var updatedContent = string.Concat(
            context.Content.AsSpan(0, verGroup.Index),
            targetVersion,
            context.Content.AsSpan(verGroup.Index + verGroup.Length));

        var dirName = !string.IsNullOrWhiteSpace(context.DirectoryName) ? context.DirectoryName : context.PackageName;
        return new SpecRewriteResult(true, updatedContent, null, HasChanged: true, SpecPath: $"SPECS/{dirName}/{context.PackageName}.spec");
    }
}

