namespace abaci_bot.Services;

/// <summary>
/// Resolves an absolute, writable default state directory across Windows, Linux containers,
/// and hardened systemd units where <see cref="Environment.SpecialFolder.LocalApplicationData"/>
/// may return an empty string.
/// </summary>
internal static class BotStatePathResolver
{
    public static string ResolveStateDirectory(string? configuredDirectory) =>
        ResolveStateDirectoryWithStatus(configuredDirectory).DirectoryPath;

    public static (string DirectoryPath, bool IsEphemeralFallback) ResolveStateDirectoryWithStatus(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return (Path.GetFullPath(configuredDirectory.Trim()), false);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData) && Path.IsPathRooted(localAppData))
        {
            var candidate = Path.GetFullPath(Path.Combine(localAppData, "abaci-bot"));
            if (IsDirectoryCreatable(candidate))
            {
                return (candidate, false);
            }
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathRooted(xdgDataHome))
        {
            var candidate = Path.GetFullPath(Path.Combine(xdgDataHome.Trim(), "abaci-bot"));
            if (IsDirectoryCreatable(candidate))
            {
                return (candidate, false);
            }
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) &&
            Path.IsPathRooted(home) &&
            !string.Equals(home.Trim(), "/", StringComparison.Ordinal))
        {
            var candidate = Path.GetFullPath(Path.Combine(home.Trim(), ".local", "share", "abaci-bot"));
            if (IsDirectoryCreatable(candidate))
            {
                return (candidate, false);
            }
        }

        return (Path.GetFullPath(Path.Combine(Path.GetTempPath(), "abaci-bot-state")), true);
    }

    private static bool IsDirectoryCreatable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
