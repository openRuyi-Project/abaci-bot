using System.Text.RegularExpressions;

namespace abaci_bot.Services;

/// <summary>
/// Parser and checker for in-spec opt-out directives (#!NoAutoUpdate: <reason>).
/// Ensures exemption reasons stay with the code, visible to all maintainers in version control.
/// </summary>
public static class NoAutoUpdateMarker
{
    private static readonly Regex MarkerRegex = new(
        @"^#!\s*NoAutoUpdate[^\S\r\n]*:[^\S\r\n]*(?<reason>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Checks whether the SPEC content contains a #!NoAutoUpdate directive.
    /// </summary>
    /// <param name="specContent">Raw text of the RPM SPEC file.</param>
    /// <param name="reason">The extracted exemption reason if present; otherwise, empty.</param>
    /// <returns>True if the SPEC opts out of automatic updates; otherwise, false.</returns>
    public static bool HasNoAutoUpdate(string specContent, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(specContent))
            return false;

        var match = MarkerRegex.Match(specContent);
        if (match.Success)
        {
            reason = match.Groups["reason"].Value.Trim();
            if (string.IsNullOrEmpty(reason))
            {
                reason = "No reason specified";
            }
            return true;
        }

        return false;
    }
}
