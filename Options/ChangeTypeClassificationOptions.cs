namespace abaci_bot.Options;

/// <summary>
/// Configuration options for PR change type static file-tree classification.
/// </summary>
public class ChangeTypeClassificationOptions
{
    /// <summary>
    /// File extensions recognized as purely documentation.
    /// </summary>
    public List<string> DocumentationExtensions { get; set; } = new() { ".md", ".txt", ".rst" };

    /// <summary>
    /// Directory prefixes recognized as documentation directories.
    /// </summary>
    public List<string> DocumentationDirectories { get; set; } = new() { "docs/", "doc/" };

    /// <summary>
    /// File extensions recognized as RPM packaging changes.
    /// </summary>
    public List<string> PackagingExtensions { get; set; } = new() { ".spec", ".patch", ".diff" };

    /// <summary>
    /// Directory paths recognized as RPM packaging areas.
    /// </summary>
    public List<string> PackagingDirectories { get; set; } = new() { "SPECS/", "SOURCES/" };

    /// <summary>
    /// File extensions recognized as frontend code.
    /// </summary>
    public List<string> FrontendExtensions { get; set; } = new() { ".vue", ".ts", ".js", ".jsx", ".tsx", ".html", ".css", ".scss", ".less" };

    /// <summary>
    /// Names of package management repositories (e.g. "openRuyi").
    /// </summary>
    public List<string> PackageRepositories { get; set; } = new() { "openRuyi" };

    /// <summary>
    /// Names of portal or documentation homepage repositories (e.g. "homepage").
    /// </summary>
    public List<string> HomepageRepositories { get; set; } = new() { "homepage" };
}
