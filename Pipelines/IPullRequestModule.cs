using abaci_bot.Contexts;

namespace abaci_bot.Pipelines;

/// <summary>
/// PR 治理流水线模块标准契约（深模块原则）
/// </summary>
public interface IPullRequestModule
{
    /// <summary>
    /// 模块唯一名称
    /// </summary>
    string ModuleName { get; }

    /// <summary>
    /// 执行优先级。数值越小越先执行（例如：Workflow=10, User=20, BuildSystem=30, AI=40）
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// 模块前置短路判断。返回 false 时该模块在当前请求跳过执行。
    /// </summary>
    bool ShouldProcess(PullRequestContext context) => true;

    /// <summary>
    /// 执行模块领域评估逻辑，仅读取上下文并向 context.LabelsToAdd / context.LabelsToRemove / context.PendingComments 声明意图。
    /// </summary>
    Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken = default);
}
