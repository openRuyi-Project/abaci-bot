namespace abaci_bot.Services;

/// <summary>
/// Helper utility for detecting AI-assisted contributions declared in pull request descriptions.
/// </summary>
public static class AiAssistanceHelper
{
    private const string CheckboxText =
        "I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.";

    /// <summary>
    /// Parses the pull request body to check if the AI-assisted contribution policy checkbox is checked.
    /// </summary>
    /// <param name="body">The pull request markdown description body.</param>
    /// <returns><c>true</c> if the checkbox line is marked as checked (<c>- [x]</c>); otherwise, <c>false</c>.</returns>
    public static bool IsAiAssistedPullRequest(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var lines = body.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (!line.Contains(CheckboxText, StringComparison.Ordinal))
                continue;

            return line.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
