using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Modules.ChangeTypeStrategies;
using abaci_bot.Options;
using abaci_bot.Services;
using NSubstitute;
using Octokit;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;

namespace abaci_bot.Tests;

public class ChangeTypeClassificationModuleTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();

    private PullRequestContext CreateContextWithFiles(
        string repo,
        IReadOnlyList<PullRequestFile> files)
    {
        _gitHub.GetPullRequestFilesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(files);

        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: repo,
            prNumber: 100,
            headSha: "sha999",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "PR Title",
            body: "PR Body",
            senderLogin: "author",
            authorLogin: "author",
            action: nameof(PullRequestAction.Opened),
            existingLabels: Array.Empty<string>(),
            prAction: PullRequestAction.Opened);
    }

    private static PullRequestFile CreateFile(string filename, string status = "modified")
    {
        var file = (PullRequestFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequestFile));
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.FileName))?.SetValue(file, filename);
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.Status))?.SetValue(file, status);
        return file;
    }

    [Fact]
    public async Task OpenRuyiRepo_WhenAllFilesAreMarkdown_AddsDocumentationLabel()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile("README.md"),
            CreateFile("docs/architecture/pipeline.md")
        };
        var context = CreateContextWithFiles("openRuyi", files);

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypeDocumentation, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TypePackaging, context.LabelsToAdd);
    }

    [Fact]
    public async Task OpenRuyiRepo_WhenSpecFileModified_AddsPackagingLabelAndNotDocumentation()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile("SPECS/gcc.spec"),
            CreateFile("README.md") // Docs also updated, but packaging takes precedence
        };
        var context = CreateContextWithFiles("openRuyi", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypePackaging, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TypeDocumentation, context.LabelsToAdd);
    }

    [Fact]
    public async Task OpenRuyiRepo_WhenPatchFileInSources_AddsPackagingLabel()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile("SOURCES/0001-fix-riscv-build.patch")
        };
        var context = CreateContextWithFiles("openRuyi", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypePackaging, context.LabelsToAdd);
    }

    [Fact]
    public async Task OpenRuyiRepo_WhenCiWorkflowModifiedWithoutSpec_AddsInfrastructureLabel()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile(".github/workflows/build-rpm.yml")
        };
        var context = CreateContextWithFiles("openRuyi", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypeInfrastructure, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TypePackaging, context.LabelsToAdd);
    }

    [Fact]
    public async Task HomepageRepo_WhenFrontendFilesModified_AddsFrontendLabel()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile("src/components/Header.vue"),
            CreateFile("src/styles/main.css")
        };
        var context = CreateContextWithFiles("homepage", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypeFrontend, context.LabelsToAdd);
    }

    [Fact]
    public async Task HomepageRepo_WhenSubmoduleModified_AddsSubmoduleUpdateLabel()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile(".gitmodules")
        };
        var context = CreateContextWithFiles("homepage", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypeSubmoduleUpdate, context.LabelsToAdd);
    }

    [Fact]
    public async Task HomepageRepo_WhenOnlyDocsModified_AddsDocumentationLabel()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile("docs/index.md"),
            CreateFile("README.txt")
        };
        var context = CreateContextWithFiles("homepage", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypeDocumentation, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TypeFrontend, context.LabelsToAdd);
    }

    [Fact]
    public async Task HomepageRepo_WhenEmptyFiles_DoesNothing()
    {
        var strategy = new HomepageRepoStrategy();
        var context = CreateContextWithFiles("homepage", Array.Empty<Octokit.PullRequestFile>());

        await strategy.EvaluateChangeTypesAsync(context, Array.Empty<Octokit.PullRequestFile>());

        Assert.Empty(context.LabelsToAdd);
    }

    [Fact]
    public async Task DefaultRepo_WhenOnlyDocsModified_AddsDocumentationLabel()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile("guide.rst")
        };
        var context = CreateContextWithFiles("random-repo", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypeDocumentation, context.LabelsToAdd);
    }

    [Fact]
    public async Task DefaultRepo_WhenCiWorkflowModified_AddsInfrastructureLabel()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile(".github/workflows/ci.yml"),
            CreateFile("src/main.rs")
        };
        var context = CreateContextWithFiles("random-repo", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypeInfrastructure, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TypeDocumentation, context.LabelsToAdd);
    }

    [Fact]
    public async Task DefaultRepo_WhenGeneralCodeModified_AddsNoLabels()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile("src/main.rs")
        };
        var context = CreateContextWithFiles("random-repo", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.LabelsToAdd);
    }

    [Fact]
    public async Task ChangeTypeClassificationModule_WhenEmptyFiles_DoesNothing()
    {
        var module = new ChangeTypeClassificationModule();
        var context = CreateContextWithFiles("openRuyi", Array.Empty<PullRequestFile>());

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.LabelsToAdd);
    }

    [Fact]
    public async Task ChangeTypeClassificationModule_WithInjectedStrategies_ExecutesCustomStrategy()
    {
        var customStrategy = Substitute.For<IChangeTypeStrategy>();
        customStrategy.AppliesTo("custom-repo").Returns(true);
        customStrategy.EvaluateChangeTypesAsync(Arg.Any<PullRequestContext>(), Arg.Any<IReadOnlyList<PullRequestFile>>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.TypePackaging);
                return Task.CompletedTask;
            });

        var module = new ChangeTypeClassificationModule(new[] { customStrategy });
        var files = new[] { CreateFile("custom.spec") };
        var context = CreateContextWithFiles("custom-repo", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypePackaging, context.LabelsToAdd);
        await customStrategy.Received(1).EvaluateChangeTypesAsync(context, Arg.Any<IReadOnlyList<PullRequestFile>>());
    }

    [Fact]
    public async Task PackageRepo_WhenDocsDirectoryContainsSpec_AddsPackagingAndExcludesDocumentation()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile("docs/test.spec")
        };
        var context = CreateContextWithFiles("openRuyi", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypePackaging, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TypeDocumentation, context.LabelsToAdd);
    }

    [Fact]
    public async Task HomepageRepo_WhenDocsDirectoryContainsHtml_AddsFrontendAndExcludesDocumentation()
    {
        var module = new ChangeTypeClassificationModule();
        var files = new[]
        {
            CreateFile("docs/index.html")
        };
        var context = CreateContextWithFiles("homepage", files);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TypeFrontend, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TypeDocumentation, context.LabelsToAdd);
    }

    [Fact]
    public async Task ChangeTypeClassificationModule_OrdersStrategiesDeterministicallyByPriority()
    {
        var lowPriorityStrategy = Substitute.For<IChangeTypeStrategy>();
        lowPriorityStrategy.Priority.Returns(100);
        lowPriorityStrategy.AppliesTo("test-repo").Returns(true);
        lowPriorityStrategy.EvaluateChangeTypesAsync(Arg.Any<PullRequestContext>(), Arg.Any<IReadOnlyList<PullRequestFile>>())
            .Returns(ci =>
            {
                ci.Arg<PullRequestContext>().LabelsToAdd.Add("Type: LowPriority");
                return Task.CompletedTask;
            });

        var highPriorityStrategy = Substitute.For<IChangeTypeStrategy>();
        highPriorityStrategy.Priority.Returns(10);
        highPriorityStrategy.AppliesTo("test-repo").Returns(true);
        highPriorityStrategy.EvaluateChangeTypesAsync(Arg.Any<PullRequestContext>(), Arg.Any<IReadOnlyList<PullRequestFile>>())
            .Returns(ci =>
            {
                ci.Arg<PullRequestContext>().LabelsToAdd.Add("Type: HighPriority");
                return Task.CompletedTask;
            });

        // Pass strategies out of order (low priority first)
        var module = new ChangeTypeClassificationModule(new[] { lowPriorityStrategy, highPriorityStrategy });
        var files = new[] { CreateFile("test.txt") };
        var context = CreateContextWithFiles("test-repo", files);

        await module.ProcessAsync(context, CancellationToken.None);

        // First applicable strategy in sorted order is highPriorityStrategy
        Assert.Contains("Type: HighPriority", context.LabelsToAdd);
        Assert.DoesNotContain("Type: LowPriority", context.LabelsToAdd);
    }
}
