using abaci_bot.Contexts;

namespace abaci_bot.Services;

/// <summary>
/// Handles repository-scoped package control plane commands (<c>ignore</c>, <c>unignore</c>, <c>status</c>)
/// and user language preference commands (<c>lang</c>).
/// </summary>
public interface IBotControlPlaneService
{
    Task HandleCommandAsync(
        PullRequestContext context,
        string botLogin,
        ParsedBotCommand command,
        string senderLogin,
        CancellationToken cancellationToken = default);
}
