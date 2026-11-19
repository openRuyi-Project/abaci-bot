namespace abaci_bot.Services;

/// <summary>
/// Context representation of an RPM SPEC file undergoing evaluation or version rewriting.
/// </summary>
/// <param name="PackageName">RPM package name (e.g. "curl", "rust-anstream-0.6").</param>
/// <param name="DirectoryName">SPEC directory name containing branch information (e.g. "rust-anstream-0.6").</param>
/// <param name="Content">Raw text content of the SPEC file.</param>
/// <param name="RelativePath">Authoritative repository-relative path to the SPEC file (e.g. "SPECS/rust-anstream-0.6/rust-anstream-0.6.spec").</param>
public record SpecContext(string PackageName, string DirectoryName, string Content, string RelativePath);

/// <summary>
/// Result of an RPM SPEC version rewrite operation.
/// </summary>
/// <param name="Success">Whether the rewrite succeeded without violating format or version constraints.</param>
/// <param name="UpdatedContent">The rewritten SPEC content if successful, or original content on failure.</param>
/// <param name="ErrorMessage">Explanation of failure if unsuccessful.</param>
/// <param name="HasChanged">Whether the content actually changed (false indicates no-op / identical content).</param>
public record SpecRewriteResult(bool Success, string UpdatedContent, string? ErrorMessage = null, bool HasChanged = false);

/// <summary>
/// Deep module abstraction for pure-function RPM SPEC version rewriting.
/// Strictly zero I/O, maintaining whitespace/tab alignment and preserving surrounding lines.
/// </summary>
public interface ISpecVersionRewriter
{
    /// <summary>
    /// Determines whether this rewriter can handle the given SPEC context.
    /// </summary>
    bool CanHandle(SpecContext context);

    /// <summary>
    /// Rewrites the SPEC content to target the specified upstream version.
    /// </summary>
    SpecRewriteResult Rewrite(SpecContext context, string targetVersion);
}

