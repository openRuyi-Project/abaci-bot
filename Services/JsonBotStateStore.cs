using System.Text.Json;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Atomic JSON-file-backed implementation of <see cref="IBotStateStore"/>.
/// Persists state to <c>bot-state.json</c> under <see cref="BotStateOptions.StateDirectory"/>
/// and holds an OS-level exclusive lock on <c>bot-state.lock</c> so at most one instance
/// writes to a given directory at a time.
/// </summary>
public sealed class JsonBotStateStore : IBotStateStore, IDisposable
{
    private readonly object _lock = new();
    private readonly string _stateFilePath;
    private readonly string _lockFilePath;
    private readonly TimeSpan _localeRetentionMaxAge;
    private readonly ILogger<JsonBotStateStore>? _logger;
    private FileStream? _directoryLockStream;
    private PersistedBotState _state = new();

    public string StateDirectory { get; }

    public JsonBotStateStore(
        IOptions<BotStateOptions> options,
        ILogger<JsonBotStateStore>? logger = null)
    {
        _logger = logger;
        var retentionDays = options.Value.LocaleRetentionDays > 0 ? options.Value.LocaleRetentionDays : 180;
        _localeRetentionMaxAge = TimeSpan.FromDays(retentionDays);
        var (resolvedDir, isEphemeral) = BotStatePathResolver.ResolveStateDirectoryWithStatus(options.Value.StateDirectory);
        StateDirectory = resolvedDir;
        if (isEphemeral)
        {
            _logger?.LogWarning(
                "BotState:StateDirectory is not configured and no persistent home/XDG data directory is writable; falling back to ephemeral temp directory '{StateDirectory}'. Persisted ignores, cooldowns, and language preferences will not survive restarts.",
                StateDirectory);
        }

        _stateFilePath = Path.Combine(StateDirectory, "bot-state.json");
        _lockFilePath = Path.Combine(StateDirectory, "bot-state.lock");
        EnsureStateDirectoryWritable();
        AcquireExclusiveDirectoryLock();
        try
        {
            LoadState();
            PruneExpiredUserLocales(_localeRetentionMaxAge);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AcquireExclusiveDirectoryLock()
    {
        try
        {
            _directoryLockStream = new FileStream(
                _lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            _logger?.LogError(
                ex,
                "BotState:StateDirectory '{StateDirectory}' is already locked by another running abaci-bot instance ({LockFile})",
                StateDirectory,
                _lockFilePath);
            throw new InvalidOperationException(
                $"BotState:StateDirectory '{StateDirectory}' is already locked by another running abaci-bot instance. Only a single active instance may share a state directory.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "Cannot open exclusive state lock file '{LockFile}'", _lockFilePath);
            throw new InvalidOperationException(
                $"BotState:StateDirectory '{StateDirectory}' lock file '{_lockFilePath}' cannot be opened exclusively.",
                ex);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _directoryLockStream?.Dispose();
            _directoryLockStream = null;
        }
    }

    private void EnsureStateDirectoryWritable()
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var probePath = Path.Combine(StateDirectory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger?.LogError(ex, "BotState:StateDirectory '{StateDirectory}' is not writable", StateDirectory);
            throw new InvalidOperationException(
                $"BotState:StateDirectory '{StateDirectory}' is not writable. Ensure the directory exists and is writable by the bot user (uid 1654 in Docker).",
                ex);
        }
    }

    public string? GetUserLocale(string userLogin)
    {
        if (string.IsNullOrWhiteSpace(userLogin))
        {
            return null;
        }

        lock (_lock)
        {
            return _state.UserLocales.TryGetValue(userLogin.Trim(), out var entry)
                ? entry.Locale
                : null;
        }
    }

    public void SetUserLocale(string userLogin, string locale)
    {
        if (string.IsNullOrWhiteSpace(userLogin) || string.IsNullOrWhiteSpace(locale))
        {
            return;
        }

        lock (_lock)
        {
            var cutoff = DateTimeOffset.UtcNow - _localeRetentionMaxAge;
            var expired = _state.UserLocales
                .Where(kv => kv.Value.UpdatedAt < cutoff)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in expired)
            {
                _state.UserLocales.Remove(key);
            }

            _state.UserLocales[userLogin.Trim()] = new UserLocaleEntry(locale.Trim(), DateTimeOffset.UtcNow);
            SaveState();
        }
    }

    public bool ClearUserLocale(string userLogin)
    {
        if (string.IsNullOrWhiteSpace(userLogin))
        {
            return false;
        }

        lock (_lock)
        {
            var removed = _state.UserLocales.Remove(userLogin.Trim());
            if (removed)
            {
                SaveState();
            }

            return removed;
        }
    }

    public int PruneExpiredUserLocales(TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        lock (_lock)
        {
            var expired = _state.UserLocales
                .Where(kv => kv.Value.UpdatedAt < cutoff)
                .Select(kv => kv.Key)
                .ToList();

            if (expired.Count == 0)
            {
                return 0;
            }

            foreach (var key in expired)
            {
                _state.UserLocales.Remove(key);
            }

            SaveState();
            return expired.Count;
        }
    }

    public IgnoredPackageEntry? GetIgnoredPackage(string owner, string repo, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
            {
                return null;
            }

            return repoState.IgnoredPackages.TryGetValue(packageName.Trim(), out var entry)
                ? entry
                : null;
        }
    }

    public IReadOnlyList<IgnoredPackageEntry> GetIgnoredPackages(string owner, string repo)
    {
        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
            {
                return Array.Empty<IgnoredPackageEntry>();
            }

            return repoState.IgnoredPackages.Values
                .OrderBy(e => e.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void SetIgnoredPackage(string owner, string repo, string packageName, string? reason, string ignoredBy)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return;
        }

        var repoKey = FormatRepoKey(owner, repo);
        var normalizedPkg = packageName.Trim();
        lock (_lock)
        {
            var repoState = GetOrCreateRepoState(repoKey);
            repoState.IgnoredPackages[normalizedPkg] = new IgnoredPackageEntry(
                normalizedPkg,
                string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                ignoredBy,
                DateTimeOffset.UtcNow);
            SaveState();
        }
    }

    public bool RemoveIgnoredPackage(string owner, string repo, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
            {
                return false;
            }

            var removed = repoState.IgnoredPackages.Remove(packageName.Trim());
            if (removed)
            {
                SaveState();
            }

            return removed;
        }
    }

    public PackageFailureEntry? GetPackageFailure(string owner, string repo, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
            {
                return null;
            }

            return repoState.PackageFailures.TryGetValue(packageName.Trim(), out var entry)
                ? entry
                : null;
        }
    }

    public void SetPackageFailure(string owner, string repo, string packageName, int consecutiveFailures, DateTimeOffset? cooldownUntil)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return;
        }

        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            var repoState = GetOrCreateRepoState(repoKey);
            repoState.PackageFailures[packageName.Trim()] = new PackageFailureEntry(consecutiveFailures, cooldownUntil);
            SaveState();
        }
    }

    public bool ClearPackageFailure(string owner, string repo, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
            {
                return false;
            }

            var removed = repoState.PackageFailures.Remove(packageName.Trim());
            if (removed)
            {
                SaveState();
            }

            return removed;
        }
    }

    public bool TryMarkFailedPrRecorded(string owner, string repo, int prNumber)
    {
        if (prNumber <= 0)
        {
            return false;
        }

        const int maxTrackedPrsPerRepo = 2048;
        var repoKey = FormatRepoKey(owner, repo);
        lock (_lock)
        {
            var repoState = GetOrCreateRepoState(repoKey);
            if (!repoState.RecordedFailedPrs.Add(prNumber))
            {
                return false;
            }

            if (repoState.RecordedFailedPrs.Count > maxTrackedPrsPerRepo)
            {
                var oldest = repoState.RecordedFailedPrs.Min();
                repoState.RecordedFailedPrs.Remove(oldest);
            }

            SaveState();
            return true;
        }
    }

    public void AddPendingEphemeralComment(PendingEphemeralComment entry)
    {
        if (entry.CommentId <= 0 || string.IsNullOrWhiteSpace(entry.Owner) || string.IsNullOrWhiteSpace(entry.Repo))
        {
            return;
        }

        lock (_lock)
        {
            _state.PendingEphemeralComments.RemoveAll(e =>
                e.CommentId == entry.CommentId &&
                string.Equals(e.Owner, entry.Owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Repo, entry.Repo, StringComparison.OrdinalIgnoreCase));
            _state.PendingEphemeralComments.Add(entry);
            SaveState();
        }
    }

    public IReadOnlyList<PendingEphemeralComment> GetDueEphemeralComments(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _state.PendingEphemeralComments
                .Where(e => e.DeleteAt <= now)
                .ToList();
        }
    }

    public void RemovePendingEphemeralComment(string owner, string repo, long commentId)
    {
        lock (_lock)
        {
            var removed = _state.PendingEphemeralComments.RemoveAll(e =>
                e.CommentId == commentId &&
                string.Equals(e.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Repo, repo, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveState();
            }
        }
    }

    public bool RecordEphemeralRetry(string owner, string repo, long commentId, DateTimeOffset nextAttemptAt, int maxRetries = 5)
    {
        lock (_lock)
        {
            var idx = _state.PendingEphemeralComments.FindIndex(e =>
                e.CommentId == commentId &&
                string.Equals(e.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Repo, repo, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                return false;
            }

            var current = _state.PendingEphemeralComments[idx];
            var nextRetry = current.RetryCount + 1;
            if (nextRetry >= maxRetries)
            {
                _state.PendingEphemeralComments.RemoveAt(idx);
                SaveState();
                return false;
            }

            _state.PendingEphemeralComments[idx] = current with { DeleteAt = nextAttemptAt, RetryCount = nextRetry };
            SaveState();
            return true;
        }
    }

    private RepoState GetOrCreateRepoState(string repoKey)
    {
        if (!_state.Repositories.TryGetValue(repoKey, out var repoState))
        {
            repoState = new RepoState();
            _state.Repositories[repoKey] = repoState;
        }

        return repoState;
    }

    private static string FormatRepoKey(string owner, string repo) => $"{owner}/{repo}";

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_stateFilePath);
            var loaded = JsonSerializer.Deserialize<PersistedBotState>(json);
            if (loaded == null)
            {
                return;
            }

            _state = new PersistedBotState
            {
                UserLocales = new Dictionary<string, UserLocaleEntry>(
                    loaded.UserLocales ?? new Dictionary<string, UserLocaleEntry>(),
                    StringComparer.OrdinalIgnoreCase),
                Repositories = new Dictionary<string, RepoState>(StringComparer.OrdinalIgnoreCase),
                PendingEphemeralComments = loaded.PendingEphemeralComments ?? new List<PendingEphemeralComment>()
            };

            if (loaded.Repositories != null)
            {
                foreach (var (repoKey, repoVal) in loaded.Repositories)
                {
                    _state.Repositories[repoKey] = new RepoState
                    {
                        IgnoredPackages = new Dictionary<string, IgnoredPackageEntry>(
                            repoVal.IgnoredPackages ?? new Dictionary<string, IgnoredPackageEntry>(),
                            StringComparer.OrdinalIgnoreCase),
                        PackageFailures = new Dictionary<string, PackageFailureEntry>(
                            repoVal.PackageFailures ?? new Dictionary<string, PackageFailureEntry>(),
                            StringComparer.OrdinalIgnoreCase),
                        RecordedFailedPrs = repoVal.RecordedFailedPrs != null
                            ? new HashSet<int>(repoVal.RecordedFailedPrs)
                            : new HashSet<int>()
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load bot state from {Path}; starting with empty state", _stateFilePath);
            _state = new PersistedBotState();
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var json = JsonSerializer.Serialize(_state);
            var tempPath = _stateFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _stateFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist bot state to {Path}", _stateFilePath);
        }
    }

    private sealed class PersistedBotState
    {
        public Dictionary<string, UserLocaleEntry> UserLocales { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RepoState> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PendingEphemeralComment> PendingEphemeralComments { get; set; } = new();
    }

    private sealed record UserLocaleEntry(string Locale, DateTimeOffset UpdatedAt);

    private sealed class RepoState
    {
        public Dictionary<string, IgnoredPackageEntry> IgnoredPackages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PackageFailureEntry> PackageFailures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> RecordedFailedPrs { get; set; } = new();
    }
}
