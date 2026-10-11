using System.IO.Compression;
using System.Text;
using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;

namespace abaci_bot.Tests;

public class RebuildImpactModuleTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    private static PullRequestFile CreateFile(string filename)
    {
        var file = (PullRequestFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequestFile));
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.FileName))?.SetValue(file, filename);
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.Status))?.SetValue(file, "modified");
        return file;
    }

    private PullRequestContext CreateContext(
        IReadOnlyList<PullRequestFile> files,
        string baseBranch = "main",
        IEnumerable<string>? existingLabels = null)
    {
        _gitHub.GetPullRequestFilesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(files);

        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: "openRuyi",
            prNumber: 55,
            headSha: "head123",
            baseBranch: baseBranch,
            isDraft: false,
            isMerged: null,
            title: "Rebuild trigger",
            body: "Rebuild impact test",
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "opened",
            existingLabels: existingLabels ?? Array.Empty<string>(),
            prAction: PullRequestAction.Opened);
    }

    private static Stream CreateGzippedXmlStream(string xmlContent)
    {
        var bytes = Encoding.UTF8.GetBytes(xmlContent);
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            gz.Write(bytes, 0, bytes.Length);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task RpmDependencyService_StreamingParse_BuildsAccurateGraph()
    {
        // glibc -> openssl -> curl -> git
        var xml = """
        <metadata xmlns="http://linux.duke.edu/metadata/common" xmlns:rpm="http://linux.duke.edu/metadata/rpm">
          <package type="rpm">
            <name>glibc</name>
            <format>
              <rpm:provides>
                <rpm:entry name="libc.so.6"/>
              </rpm:provides>
            </format>
          </package>
          <package type="rpm">
            <name>openssl</name>
            <format>
              <rpm:provides>
                <rpm:entry name="libssl.so.3"/>
              </rpm:provides>
              <rpm:requires>
                <rpm:entry name="libc.so.6"/>
              </rpm:requires>
            </format>
          </package>
          <package type="rpm">
            <name>curl</name>
            <format>
              <rpm:provides>
                <rpm:entry name="libcurl.so.4"/>
              </rpm:provides>
              <rpm:requires>
                <rpm:entry name="libssl.so.3"/>
              </rpm:requires>
            </format>
          </package>
          <package type="rpm">
            <name>git</name>
            <format>
              <rpm:requires>
                <rpm:entry name="libcurl.so.4"/>
              </rpm:requires>
            </format>
          </package>
        </metadata>
        """;

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { CalculationMode = "Transitive" });
        var service = new RpmDependencyService(_httpClientFactory, options);

        using var stream = CreateGzippedXmlStream(xml);
        await service.LoadMetadataAsync(stream, isGzipped: true);

        // Transitive: glibc affects openssl, curl, git (3 downstream packages)
        var affectedByGlibc = service.GetAffectedPackages("glibc");
        Assert.Equal(3, affectedByGlibc.Count);
        Assert.Contains("openssl", affectedByGlibc);
        Assert.Contains("curl", affectedByGlibc);
        Assert.Contains("git", affectedByGlibc);

        // MaxDepth = 1: glibc only affects direct dependent openssl
        var directOnly = service.GetAffectedPackages("glibc", maxDepth: 1);
        Assert.Single(directOnly);
        Assert.Contains("openssl", directOnly);
    }

    [Theory]
    [InlineData(0, GitHubLabels.Rebuild0)]
    [InlineData(5, GitHubLabels.Rebuild1To10)]
    [InlineData(50, GitHubLabels.Rebuild11To100)]
    [InlineData(300, GitHubLabels.Rebuild101To500)]
    [InlineData(800, GitHubLabels.Rebuild501To1000)]
    [InlineData(1500, GitHubLabels.Rebuild1001To2500)]
    [InlineData(4000, GitHubLabels.Rebuild2501To5000)]
    [InlineData(9000, GitHubLabels.Rebuild5001Plus)]
    public void MapAffectedCountToRebuildLabel_AccurateTierMapping(int count, string expectedLabel)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions());
        var service = new RpmDependencyService(_httpClientFactory, options);
        var label = service.MapAffectedCountToRebuildLabel(count);
        Assert.Equal(expectedLabel, label);
    }

    [Fact]
    public async Task ProcessAsync_ModerateImpact_AssignsRebuildLabelWithoutBlocking()
    {
        var dependencyService = Substitute.For<IRpmDependencyService>();
        dependencyService.GetAffectedPackages("testpkg", Arg.Any<int?>())
            .Returns(new HashSet<string> { "dep1", "dep2", "dep3" });
        dependencyService.MapAffectedCountToRebuildLabel(3).Returns(GitHubLabels.Rebuild1To10);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { BlockThreshold = 1001 });
        var module = new RebuildImpactModule(dependencyService, options);

        var file = CreateFile("SPECS/testpkg/testpkg.spec");
        var context = CreateContext(new[] { file });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.Rebuild1To10, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.WorkflowBlocked, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task ProcessAsync_HighImpactOnRolling_EnforcesAdmissionBlockAndComment()
    {
        var dependencyService = Substitute.For<IRpmDependencyService>();
        var largeSet = Enumerable.Range(1, 1200).Select(i => $"pkg-{i}").ToHashSet();
        dependencyService.GetAffectedPackages("glibc", Arg.Any<int?>()).Returns(largeSet);
        dependencyService.MapAffectedCountToRebuildLabel(1200).Returns(GitHubLabels.Rebuild1001To2500);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { BlockThreshold = 1001 });
        var module = new RebuildImpactModule(dependencyService, options);

        var file = CreateFile("SPECS/glibc/glibc.spec");
        var context = CreateContext(new[] { file }, baseBranch: "main");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.Rebuild1001To2500, context.LabelsToAdd);
        Assert.Contains(GitHubLabels.WorkflowBlocked, context.LabelsToAdd);
        Assert.NotEmpty(context.PendingComments);
        Assert.Contains("Rebuild 规模准入拦截", context.PendingComments[0]);
        Assert.Contains("1200", context.PendingComments[0]);
        Assert.Contains("Staging", context.PendingComments[0]);
    }

    [Fact]
    public async Task ProcessAsync_HighImpactOnStagingBranch_DoesNotBlock()
    {
        var dependencyService = Substitute.For<IRpmDependencyService>();
        var largeSet = Enumerable.Range(1, 1500).Select(i => $"pkg-{i}").ToHashSet();
        dependencyService.GetAffectedPackages("glibc", Arg.Any<int?>()).Returns(largeSet);
        dependencyService.MapAffectedCountToRebuildLabel(1500).Returns(GitHubLabels.Rebuild1001To2500);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { BlockThreshold = 1001 });
        var module = new RebuildImpactModule(dependencyService, options);

        var file = CreateFile("SPECS/glibc/glibc.spec");
        var context = CreateContext(new[] { file }, baseBranch: "staging-24.09");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.Rebuild1001To2500, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.WorkflowBlocked, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task RpmDependencyService_ZstdMetadataStream_DecompressesAndBuildsGraph()
    {
        var xml = """
        <metadata xmlns="http://linux.duke.edu/metadata/common" xmlns:rpm="http://linux.duke.edu/metadata/rpm">
          <package type="rpm">
            <name>kernel</name>
            <format>
              <rpm:provides>
                <rpm:entry name="kernel-core"/>
              </rpm:provides>
            </format>
          </package>
          <package type="rpm">
            <name>kernel-modules</name>
            <format>
              <rpm:requires>
                <rpm:entry name="kernel-core"/>
              </rpm:requires>
            </format>
          </package>
        </metadata>
        """;

        using var compressor = new ZstdSharp.Compressor();
        var compressedBytes = compressor.Wrap(Encoding.UTF8.GetBytes(xml)).ToArray();
        using var stream = new MemoryStream(compressedBytes);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { CalculationMode = "Transitive" });
        var service = new RpmDependencyService(_httpClientFactory, options);

        await service.LoadMetadataAsync(stream);

        var affected = service.GetAffectedPackages("kernel");
        Assert.Single(affected);
        Assert.Contains("kernel-modules", affected);
    }

    [Fact]
    public async Task ProcessAsync_ImpactDropsBelowThreshold_RemovesWorkflowBlockedLabel()
    {
        var dependencyService = Substitute.For<IRpmDependencyService>();
        dependencyService.GetAffectedPackages("leafpkg", Arg.Any<int?>())
            .Returns(new HashSet<string> { "dep1" });
        dependencyService.MapAffectedCountToRebuildLabel(1).Returns(GitHubLabels.Rebuild1To10);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { BlockThreshold = 1001 });
        var module = new RebuildImpactModule(dependencyService, options);

        var file = CreateFile("SPECS/leafpkg/leafpkg.spec");
        var context = CreateContext(new[] { file }, baseBranch: "main", existingLabels: new[] { GitHubLabels.WorkflowBlocked });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.WorkflowBlocked, context.LabelsToRemove);
        Assert.DoesNotContain(GitHubLabels.WorkflowBlocked, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task RpmDependencyService_ScriptletPathsLikeBinSh_AreExcludedFromDAG()
    {
        var xml = """
        <metadata xmlns="http://linux.duke.edu/metadata/common" xmlns:rpm="http://linux.duke.edu/metadata/rpm">
          <package type="rpm">
            <name>bash</name>
            <format>
              <rpm:provides>
                <rpm:entry name="/bin/sh"/>
                <rpm:entry name="/bin/bash"/>
              </rpm:provides>
            </format>
          </package>
          <package type="rpm">
            <name>myapp</name>
            <format>
              <rpm:requires>
                <rpm:entry name="/bin/sh"/>
              </rpm:requires>
            </format>
          </package>
        </metadata>
        """;

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { CalculationMode = "Transitive" });
        var service = new RpmDependencyService(_httpClientFactory, options);

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await service.LoadMetadataAsync(ms, isGzipped: false);

        // myapp requires /bin/sh which is a scriptlet interpreter path, not a rebuild dependency
        var affectedByBash = service.GetAffectedPackages("bash");
        Assert.Empty(affectedByBash);
    }
}


