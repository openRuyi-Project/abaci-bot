using System.Reflection;

namespace abaci_bot.Services;

/// <summary>
/// Exposes immutable build and deployment identity metadata for the <c>/health</c> endpoint
/// without leaking secrets, filesystem paths, or raw environment variables.
/// </summary>
public static class BuildIdentity
{
    public static string Version { get; } = ResolveVersion();

    public static string Commit { get; } = ResolveCommit();

    public static string ImageDigest { get; } = ResolveImageDigest();

    public static string BuildTime { get; } = ResolveBuildTime();

    public static string Deployment { get; } = ResolveDeployment();

    private static Assembly EntryAssembly => typeof(BuildIdentity).Assembly;

    private static string ResolveVersion()
    {
        var info = EntryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plusIndex = info.IndexOf('+');
            return plusIndex > 0 ? info[..plusIndex] : info;
        }

        return EntryAssembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private static string ResolveCommit()
    {
        var info = EntryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var envCommit = Environment.GetEnvironmentVariable("ABACI_BUILD_COMMIT");
        var envDirty = string.Equals(
            Environment.GetEnvironmentVariable("ABACI_BUILD_DIRTY"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        return ResolveCommitFrom(info, envCommit, envDirty);
    }

    internal static string ResolveCommitFrom(string? informationalVersion, string? envCommit, bool envDirty)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+');
            if (plusIndex >= 0 && plusIndex + 1 < informationalVersion.Length)
            {
                var commit = informationalVersion[(plusIndex + 1)..].Trim();
                if (!string.IsNullOrEmpty(commit) &&
                    !string.Equals(commit, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    return FormatCommitWithDirty(commit, isDirty: false);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(envCommit) &&
            !string.Equals(envCommit.Trim(), "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return FormatCommitWithDirty(envCommit.Trim(), envDirty);
        }

        return "unknown";
    }

    internal static string FormatCommitWithDirty(string rawCommit, bool isDirty)
    {
        const string dirtySuffix = "-dirty";
        var hasDirtySuffix = rawCommit.EndsWith(dirtySuffix, StringComparison.OrdinalIgnoreCase);
        var sha = hasDirtySuffix ? rawCommit[..^dirtySuffix.Length] : rawCommit;
        if (sha.Length > 12)
        {
            sha = sha[..12];
        }

        return (hasDirtySuffix || isDirty) ? sha + dirtySuffix : sha;
    }

    private static string ResolveImageDigest()
    {
        var digest = Environment.GetEnvironmentVariable("ABACI_IMAGE_DIGEST");
        return string.IsNullOrWhiteSpace(digest) ? "unknown" : digest.Trim();
    }

    private static string ResolveBuildTime()
    {
        var attr = EntryAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "BuildTime", StringComparison.OrdinalIgnoreCase));

        return !string.IsNullOrWhiteSpace(attr?.Value) ? attr.Value : "unknown";
    }

    private static string ResolveDeployment()
    {
        var runningInContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (runningInContainer || File.Exists("/.dockerenv"))
        {
            return "container";
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
        {
            return "systemd";
        }

        return "binary";
    }
}
