namespace abaci_bot.Services;

/// <summary>
/// Authoritative identifiers returned when creating a GitHub issue/PR comment.
/// </summary>
public sealed record CreatedCommentRef(long Id, string? NodeId);

/// <summary>
/// Narrow GitHub comment lifecycle port for creating tracked replies and deleting or minimizing expired ones.
/// </summary>
public interface IEphemeralCommentGateway
{
    Task<CreatedCommentRef> CreateCommentWithMetadataAsync(string owner, string repo, int number, string comment, CancellationToken ct = default);
    Task DeleteCommentAsync(string owner, string repo, long commentId, CancellationToken ct = default);
    Task<bool> MinimizeCommentAsync(string owner, string repo, long commentId, string? subjectNodeId = null, string classifier = "SPAM", CancellationToken ct = default);
}
