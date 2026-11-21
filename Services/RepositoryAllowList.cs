using Microsoft.Extensions.Logging;

namespace abaci_bot.Services;

/// <summary>
/// Deep module centralizing strict "owner/repo" allow-list validation, parsing, and admission.
/// Enforces fail-closed semantics: an empty or null allow-list denies all repositories,
/// and bare repository names without an owner prefix are rejected.
/// </summary>
public static class RepositoryAllowList
{
    /// <summary>
    /// Attempts to parse a repository entry formatted strictly as "owner/repo".
    /// </summary>
    public static bool TryParseRepository(string? entry, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var parts = entry.Trim().Split('/');
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        var parsedOwner = parts[0].Trim();
        var parsedRepo = parts[1].Trim();
        if (parsedOwner.Any(char.IsWhiteSpace) || parsedRepo.Any(char.IsWhiteSpace))
        {
            return false;
        }

        owner = parsedOwner;
        repo = parsedRepo;
        return true;
    }

    /// <summary>
    /// Determines whether the target repository is permitted by the configured allow-list.
    /// Returns <c>false</c> when <paramref name="allowList"/> is null or empty or lacks an explicit "owner/repo" match.
    /// </summary>
    public static bool IsAllowed(string owner, string repo, IReadOnlyCollection<string>? allowList)
    {
        if (allowList == null || allowList.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return false;
        }

        foreach (var entry in allowList)
        {
            if (TryParseRepository(entry, out var allowedOwner, out var allowedRepo) &&
                string.Equals(allowedOwner, owner.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(allowedRepo, repo.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses valid "owner/repo" entries from a configured allow-list, logging warnings for malformed entries.
    /// </summary>
    public static List<(string Owner, string Repo)> ParseRepositories(
        IReadOnlyCollection<string>? allowList,
        ILogger? logger = null,
        string configSection = "RepositoryAllowList")
    {
        var list = new List<(string Owner, string Repo)>();

        if (allowList == null || allowList.Count == 0)
        {
            return list;
        }

        foreach (var entry in allowList)
        {
            if (TryParseRepository(entry, out var parsedOwner, out var parsedRepo))
            {
                list.Add((parsedOwner, parsedRepo));
            }
            else
            {
                logger?.LogWarning(
                    "Invalid repository entry '{Entry}' in {ConfigSection}. Expected format 'owner/repo'.",
                    entry,
                    configSection);
            }
        }

        return list;
    }

    /// <summary>
    /// Returns all entries in <paramref name="allowList"/> that fail strict "owner/repo" parsing.
    /// </summary>
    public static IReadOnlyList<string> FindInvalidEntries(IReadOnlyCollection<string>? allowList)
    {
        if (allowList == null || allowList.Count == 0)
        {
            return Array.Empty<string>();
        }

        var invalid = new List<string>();
        foreach (var entry in allowList)
        {
            if (!TryParseRepository(entry, out _, out _))
            {
                invalid.Add(entry ?? string.Empty);
            }
        }

        return invalid;
    }
}
