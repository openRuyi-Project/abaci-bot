namespace abaci_bot.Services;

public sealed record ParsedBotCommand(string Verb, string Arguments);

/// <summary>
/// Parses GitHub mention commands addressed to the bot (<c>@&lt;botLogin&gt; &lt;verb&gt; [args]</c>).
/// Matches case-insensitively and strips optional <c>[bot]</c> suffix and trailing <c>:</c> or <c>,</c>.
/// </summary>
public static class BotCommandParser
{
    public static bool TryParse(string? commentBody, string botLogin, out ParsedBotCommand command)
    {
        command = default!;
        if (string.IsNullOrWhiteSpace(commentBody) || string.IsNullOrWhiteSpace(botLogin))
        {
            return false;
        }

        var trimmed = commentBody.TrimStart();
        if (!trimmed.StartsWith('@'))
        {
            return false;
        }

        // Extract first line and token after '@'
        var lineEnd = trimmed.IndexOfAny(['\r', '\n']);
        var firstLine = (lineEnd >= 0 ? trimmed[..lineEnd] : trimmed).Trim();

        var firstSpace = firstLine.IndexOfAny([' ', '\t']);
        if (firstSpace < 0)
        {
            return false;
        }

        var mentionToken = firstLine[1..firstSpace].TrimEnd(':', ',');
        if (!MatchesBotLogin(mentionToken, botLogin))
        {
            return false;
        }

        var remainder = firstLine[(firstSpace + 1)..].Trim();
        if (string.IsNullOrEmpty(remainder))
        {
            return false;
        }

        var verbEnd = remainder.IndexOfAny([' ', '\t']);
        var verb = (verbEnd >= 0 ? remainder[..verbEnd] : remainder).Trim().ToLowerInvariant();
        var args = verbEnd >= 0 ? remainder[(verbEnd + 1)..].Trim() : string.Empty;

        if (string.IsNullOrEmpty(verb))
        {
            return false;
        }

        command = new ParsedBotCommand(verb, args);
        return true;
    }

    private static bool MatchesBotLogin(string mentionToken, string botLogin)
    {
        var normalizedMention = StripBotSuffix(mentionToken.Trim());
        var normalizedBot = StripBotSuffix(botLogin.Trim());
        return !string.IsNullOrEmpty(normalizedMention) &&
               string.Equals(normalizedMention, normalizedBot, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripBotSuffix(string login)
    {
        const string suffix = "[bot]";
        return login.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? login[..^suffix.Length]
            : login;
    }
}
