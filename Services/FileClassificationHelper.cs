namespace abaci_bot.Services;

/// <summary>
/// Helper predicates for categorizing pull request files (documentation, CI workflows, etc.).
/// Follows the static helper pattern established by <see cref="BotDetectionHelper"/>.
/// </summary>
public static class FileClassificationHelper
{
    /// <summary>
    /// Checks if the specified file path represents a documentation file based on configured options.
    /// </summary>
    public static bool IsDocumentationFile(string path, abaci_bot.Options.ChangeTypeClassificationOptions options)
    {
        return options.DocumentationExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ||
               options.DocumentationDirectories.Any(dir => path.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if the specified file path represents a CI workflow or GitHub action configuration.
    /// </summary>
    public static bool IsCiFile(string path)
    {
        return path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase);
    }
}
