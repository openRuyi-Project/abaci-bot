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
        IEnumerable<string>? existingLabels = null,
        PullRequestAction? prAction = null)
    {
        var effectiveAction = prAction ?? PullRequestAction.Opened;
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
            action: effectiveAction.ToString().ToLowerInvariant(),
            existingLabels: existingLabels ?? Array.Empty<string>(),
            prAction: effectiveAction);
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
        Assert.Contains("Rebuild Impact Gate: Merge Blocked", context.PendingComments[0]);
        Assert.Contains("1200", context.PendingComments[0]);
        Assert.Contains("Staging", context.PendingComments[0]);
    }

    [Fact]
    public async Task ProcessAsync_HighImpactOnRolling_AlreadyBlocked_DoesNotDuplicateComment()
    {
        var dependencyService = Substitute.For<IRpmDependencyService>();
        var largeSet = Enumerable.Range(1, 1200).Select(i => $"pkg-{i}").ToHashSet();
        dependencyService.GetAffectedPackages("glibc", Arg.Any<int?>()).Returns(largeSet);
        dependencyService.MapAffectedCountToRebuildLabel(1200).Returns(GitHubLabels.Rebuild1001To2500);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { BlockThreshold = 1001 });
        var module = new RebuildImpactModule(dependencyService, options);

        var file = CreateFile("SPECS/glibc/glibc.spec");
        var context = CreateContext(
            new[] { file },
            baseBranch: "main",
            existingLabels: new[] { GitHubLabels.WorkflowBlocked, GitHubLabels.Rebuild1001To2500 },
            prAction: PullRequestAction.Edited);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.Rebuild1001To2500, context.LabelsToAdd);
        Assert.Contains(GitHubLabels.WorkflowBlocked, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
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
    public async Task RpmDependencyService_ChunkedNonSeekableZstdStream_DetectsMagicAndLoadsMetadata()
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
        using var stream = new OneByteChunkedStream(compressedBytes);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { CalculationMode = "Transitive" });
        var service = new RpmDependencyService(_httpClientFactory, options);

        await service.LoadMetadataAsync(stream);

        var affected = service.GetAffectedPackages("kernel");
        Assert.Single(affected);
        Assert.Contains("kernel-modules", affected);
    }

    private class OneByteChunkedStream : Stream
    {
        private readonly byte[] _data;
        private int _pos;

        public OneByteChunkedStream(byte[] data) => _data = data;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length || count <= 0) return 0;
            buffer[offset] = _data[_pos++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= _data.Length || buffer.Length <= 0) return ValueTask.FromResult(0);
            buffer.Span[0] = _data[_pos++];
            return ValueTask.FromResult(1);
        }
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
        var context = CreateContext(new[] { file }, baseBranch: "main", existingLabels: new[] { GitHubLabels.WorkflowBlocked, GitHubLabels.Rebuild1001To2500 });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.WorkflowBlocked, context.LabelsToRemove);
        Assert.DoesNotContain(GitHubLabels.WorkflowBlocked, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task ProcessAsync_RetargetFromRollingToLts_ClearsWorkflowBlockedLabel()
    {
        var dependencyService = Substitute.For<IRpmDependencyService>();
        var largeSet = Enumerable.Range(1, 1500).Select(i => $"pkg-{i}").ToHashSet();
        dependencyService.GetAffectedPackages("glibc", Arg.Any<int?>()).Returns(largeSet);
        dependencyService.MapAffectedCountToRebuildLabel(1500).Returns(GitHubLabels.Rebuild1001To2500);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { BlockThreshold = 1001, TargetBranchToBlock = "Rolling" });
        var module = new RebuildImpactModule(dependencyService, options);

        var file = CreateFile("SPECS/glibc/glibc.spec");
        // PR was previously blocked on main (Rolling), then retargeted to LTS branch via Edited action without new commits
        var context = CreateContext(
            new[] { file },
            baseBranch: "lts-24.03",
            prAction: PullRequestAction.Edited,
            existingLabels: new[] { GitHubLabels.WorkflowBlocked, GitHubLabels.Rebuild1001To2500 });

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.WorkflowBlocked, context.LabelsToRemove);
        Assert.DoesNotContain(GitHubLabels.WorkflowBlocked, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task ProcessAsync_HumanBlockedWithoutRebuildLabel_DoesNotUnblock()
    {
        var dependencyService = Substitute.For<IRpmDependencyService>();
        dependencyService.GetAffectedPackages("leafpkg", Arg.Any<int?>())
            .Returns(new HashSet<string> { "dep1" });
        dependencyService.MapAffectedCountToRebuildLabel(1).Returns(GitHubLabels.Rebuild1To10);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { BlockThreshold = 1001 });
        var module = new RebuildImpactModule(dependencyService, options);

        // PR was blocked by a human maintainer without any rebuild tier label
        var file = CreateFile("SPECS/leafpkg/leafpkg.spec");
        var context = CreateContext(new[] { file }, baseBranch: "main", existingLabels: new[] { GitHubLabels.WorkflowBlocked });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.DoesNotContain(GitHubLabels.WorkflowBlocked, context.LabelsToRemove);
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

    [Fact]
    public void ExtractPrimaryLocationFromRepomd_ValidRepomdXml_ExtractsPrimaryLocationHref()
    {
        var repomdXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <repomd xmlns="http://linux.duke.edu/metadata/repo">
          <data type="other">
            <location href="repodata/other.xml.gz"/>
          </data>
          <data type="primary">
            <location href="repodata/cc770dc7d453b304ee20d67373e4b033323267ed7ba33069583d97ac2cf1a5d5-primary.xml.gz"/>
            <timestamp>1728900000</timestamp>
          </data>
          <data type="filelists">
            <location href="repodata/filelists.xml.gz"/>
          </data>
        </repomd>
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(repomdXml));
        var href = RpmDependencyService.ExtractPrimaryLocationFromRepomd(stream);

        Assert.Equal("repodata/cc770dc7d453b304ee20d67373e4b033323267ed7ba33069583d97ac2cf1a5d5-primary.xml.gz", href);
    }

    [Fact]
    public async Task ResolveEffectivePrimaryUrlAsync_ConfiguredWithRepomd_ResolvesFullPrimaryUrl()
    {
        var repomdXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <repomd xmlns="http://linux.duke.edu/metadata/repo">
          <data type="primary">
            <location href="repodata/1234abcd-primary.xml.gz"/>
          </data>
        </repomd>
        """;

        var handler = new HttpHandlerStub(req =>
        {
            if (req.RequestUri!.ToString().EndsWith("repomd.xml"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(repomdXml, Encoding.UTF8, "application/xml")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions());
        var service = new RpmDependencyService(_httpClientFactory, options);

        var resolved = await service.ResolveEffectivePrimaryUrlAsync(
            client,
            "https://repo.openeuler.org/openEuler-24.03-LTS/OS/x86_64/repodata/repomd.xml",
            CancellationToken.None);

        Assert.Equal("https://repo.openeuler.org/openEuler-24.03-LTS/OS/x86_64/repodata/1234abcd-primary.xml.gz", resolved);
    }

    [Fact]
    public async Task ResolveEffectivePrimaryUrlAsync_HashedPrimaryReturns404_FallsBackToRepomd()
    {
        var repomdXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <repomd xmlns="http://linux.duke.edu/metadata/repo">
          <data type="primary">
            <location href="repodata/new-primary.xml.gz"/>
          </data>
        </repomd>
        """;

        var handler = new HttpHandlerStub(req =>
        {
            if (req.RequestUri!.ToString().Contains("old-primary.xml.gz"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }
            if (req.RequestUri!.ToString().EndsWith("repomd.xml"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(repomdXml, Encoding.UTF8, "application/xml")
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        var client = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions());
        var service = new RpmDependencyService(_httpClientFactory, options);

        var resolved = await service.ResolveEffectivePrimaryUrlAsync(
            client,
            "https://repo.openeuler.org/openEuler-24.03-LTS/OS/x86_64/repodata/old-primary.xml.gz",
            CancellationToken.None);

        Assert.Equal("https://repo.openeuler.org/openEuler-24.03-LTS/OS/x86_64/repodata/new-primary.xml.gz", resolved);
    }

    [Fact]
    public async Task RefreshMetadataAsync_EmptyOptions_ReturnsFalseSafely()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
        {
            LocalCachePath = null,
            PrimaryXmlUrl = null
        });
        var service = new RpmDependencyService(_httpClientFactory, options);

        var result = await service.RefreshMetadataAsync(CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task RefreshMetadataAsync_WithValidLocalGzipCache_LoadsSuccessfully()
    {
        var xml = """
        <metadata xmlns="http://linux.duke.edu/metadata/common" xmlns:rpm="http://linux.duke.edu/metadata/rpm">
          <package type="rpm">
            <name>local-pkg-a</name>
            <format>
              <rpm:provides><rpm:entry name="liblocala.so.1"/></rpm:provides>
            </format>
          </package>
        </metadata>
        """;

        var tempGz = Path.Combine(Path.GetTempPath(), $"test-cache-{Guid.NewGuid():N}.xml.gz");
        try
        {
            using (var fs = File.Create(tempGz))
            using (var gz = new GZipStream(fs, CompressionMode.Compress))
            {
                var bytes = Encoding.UTF8.GetBytes(xml);
                await gz.WriteAsync(bytes);
            }

            var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
            {
                LocalCachePath = tempGz
            });
            var service = new RpmDependencyService(_httpClientFactory, options);

            var success = await service.RefreshMetadataAsync(CancellationToken.None);
            Assert.True(success);
            Assert.Contains(GitHubLabels.Rebuild0, service.MapAffectedCountToRebuildLabel(0));
        }
        finally
        {
            if (File.Exists(tempGz)) File.Delete(tempGz);
        }
    }

    [Fact]
    public async Task RefreshMetadataAsync_WithCorruptLocalCache_CatchesAndFallsThrough()
    {
        var tempCorrupt = Path.Combine(Path.GetTempPath(), $"test-corrupt-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tempCorrupt, "corrupt non-xml data");

            var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
            {
                LocalCachePath = tempCorrupt,
                PrimaryXmlUrl = null
            });
            var service = new RpmDependencyService(_httpClientFactory, options);

            var success = await service.RefreshMetadataAsync(CancellationToken.None);
            Assert.False(success);
        }
        finally
        {
            if (File.Exists(tempCorrupt)) File.Delete(tempCorrupt);
        }
    }

    [Fact]
    public async Task RefreshMetadataAsync_WithRemoteUrl_SuccessLoadsMetadata()
    {
        var xml = """
        <metadata xmlns="http://linux.duke.edu/metadata/common" xmlns:rpm="http://linux.duke.edu/metadata/rpm">
          <package type="rpm">
            <name>remote-pkg-x</name>
            <format>
              <rpm:provides><rpm:entry name="libremotex.so.1"/></rpm:provides>
            </format>
          </package>
        </metadata>
        """;

        var handler = new HttpHandlerStub(req =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        var client = new HttpClient(handler);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("RpmMetadataClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
        {
            LocalCachePath = null,
            PrimaryXmlUrl = "https://repo.example.org/primary.xml"
        });
        var service = new RpmDependencyService(clientFactory, options);

        var success = await service.RefreshMetadataAsync(CancellationToken.None);
        Assert.True(success);
    }

    [Fact]
    public async Task RefreshMetadataAsync_WithRemoteUrl_HttpError_ReturnsFalse()
    {
        var handler = new HttpHandlerStub(req =>
            new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));

        var client = new HttpClient(handler);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("RpmMetadataClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
        {
            LocalCachePath = null,
            PrimaryXmlUrl = "https://repo.example.org/primary.xml"
        });
        var service = new RpmDependencyService(clientFactory, options);

        var success = await service.RefreshMetadataAsync(CancellationToken.None);
        Assert.False(success);
    }

    private sealed class HttpHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public HttpHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}


