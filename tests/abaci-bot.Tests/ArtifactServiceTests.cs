using System.IO.Compression;
using System.Net;
using System.Text;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace abaci_bot.Tests;

public class ArtifactServiceTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task DownloadAndExtractRpmArtifactAsync_PathTraversalZipEntry_IsSafelySkipped()
    {
        // Construct a zip file in memory containing a normal entry and a Zip Slip traversal entry
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var validEntry = archive.CreateEntry("valid-pkg.rpm");
            using (var writer = new StreamWriter(validEntry.Open()))
            {
                writer.Write("rpm-data");
            }

            var traversalEntry = archive.CreateEntry("../../evil.rpm");
            using (var writer = new StreamWriter(traversalEntry.Open()))
            {
                writer.Write("malicious-payload");
            }
        }
        ms.Position = 0;

        var gitHubService = Substitute.For<IGitHubService>();
        gitHubService.DownloadArtifactZipStreamAsync("owner", "repo", 12345, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(ms.ToArray())));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var service = new ArtifactService(gitHubService, _httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var extracted = await service.DownloadAndExtractRpmArtifactAsync("owner", "repo", 12345, tempDir);

            // Only valid-pkg.rpm should be extracted
            Assert.Single(extracted);
            Assert.EndsWith("valid-pkg.rpm", extracted[0]);
            Assert.True(File.Exists(extracted[0]));

            // Ensure evil.rpm was NOT created outside tempDir
            var outsidePath = Path.GetFullPath(Path.Combine(tempDir, "../../evil.rpm"));
            Assert.False(File.Exists(outsidePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadBaselineRpmAsync_MissingUrlOption_ReturnsNull()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            StableRpmBaseUrl = null
        });
        var service = new ArtifactService(_gitHub, _httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        var result = await service.DownloadBaselineRpmAsync("glibc", tempDir);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadRunRpmArtifactsAsync_ParsesArtifactListAndExtractsRpms()
    {
        var artifactListJson = """
        {
            "total_count": 1,
            "artifacts": [
                {
                    "id": 99901,
                    "name": "rpms-x86_64"
                }
            ]
        }
        """;

        using var zipMs = new MemoryStream();
        using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("libfoo-1.0.0.rpm");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("rpm-binary");
        }
        zipMs.Position = 0;

        var handler = new DelegatingHandlerStub(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/artifacts"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(artifactListJson) };
            }
            if (req.RequestUri.AbsolutePath.EndsWith("/zip"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipMs.ToArray()) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var gitHubService = Substitute.For<IGitHubService>();
        gitHubService.ListWorkflowRunArtifactsAsync("owner", "repo", 55555, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(long Id, string Name, long SizeInBytes)>>(
                new (long, string, long)[] { (12345, "build-rpms", 1000) }));
        gitHubService.DownloadArtifactZipStreamAsync("owner", "repo", 12345, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(zipMs.ToArray())));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var service = new ArtifactService(gitHubService, _httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var rpms = await service.DownloadRunRpmArtifactsAsync("owner", "repo", 55555, tempDir);
            Assert.Single(rpms);
            Assert.EndsWith("libfoo-1.0.0.rpm", rpms[0]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("libgpg-error-1.61-999.or.x86_64.rpm", "libgpg-error")]
    [InlineData("python3-dnf-4.18-999.or.noarch.rpm", "python3-dnf")]
    [InlineData("dirmngr-2.5.24-999.or.x86_64.rpm", "dirmngr")]
    [InlineData("gnupg-2.5.24-999.or.x86_64.rpm", "gnupg")]
    [InlineData("libgcrypt-1.12.4-999.or.x86_64.rpm", "libgcrypt")]
    [InlineData("zlib-ng-compat-2.1.3-1.oe2403.riscv64.rpm", "zlib-ng-compat")]
    [InlineData("bash-5.2-1.fc39.x86_64.rpm", "bash")]
    [InlineData("my-pkg-1.0.rpm", "my-pkg")]
    [InlineData("simple.rpm", "simple")]
    public void ExtractPackageName_CorrectlyExtractsPackageName(string fileName, string expectedName)
    {
        var actual = ArtifactService.ExtractPackageName(fileName);
        Assert.Equal(expectedName, actual);
    }

    [Fact]
    public async Task DownloadBaselineRpmAsync_WithRpmDependencyServiceLocation_UsesLocationHref()
    {
        string? requestedUrl = null;
        var handler = new DelegatingHandlerStub(req =>
        {
            requestedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("rpm-baseline-bytes"))
            };
        });

        var client = new HttpClient(handler);
        _httpClientFactory.CreateClient("StableRpmClient").Returns(client);

        var rpmDepService = Substitute.For<IRpmDependencyService>();
        rpmDepService.GetPackageLocation("libgpg-error")
            .Returns("Packages/libgpg-error-1.47-1.oe2403.x86_64.rpm");

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            StableRpmBaseUrl = "https://repo.openeuler.org/openEuler-24.03-LTS/OS/x86_64"
        });

        var service = new ArtifactService(_gitHub, _httpClientFactory, options, rpmDepService);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var downloaded = await service.DownloadBaselineRpmAsync("libgpg-error", tempDir);
            Assert.NotNull(downloaded);
            Assert.True(File.Exists(downloaded));
            Assert.Equal("https://repo.openeuler.org/openEuler-24.03-LTS/OS/x86_64/Packages/libgpg-error-1.47-1.oe2403.x86_64.rpm", requestedUrl);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadBaselineRpmAsync_WithoutLocationHref_FallsBackToDirectRpmUrl()
    {
        string? requestedUrl = null;
        var handler = new DelegatingHandlerStub(req =>
        {
            requestedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("rpm-baseline-bytes"))
            };
        });

        var client = new HttpClient(handler);
        _httpClientFactory.CreateClient("StableRpmClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            StableRpmBaseUrl = "https://repo.openeuler.org/baselines"
        });

        var service = new ArtifactService(_gitHub, _httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var downloaded = await service.DownloadBaselineRpmAsync("libgpg-error", tempDir);
            Assert.NotNull(downloaded);
            Assert.True(File.Exists(downloaded));
            Assert.Equal("https://repo.openeuler.org/baselines/libgpg-error.rpm", requestedUrl);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadRunRpmArtifactsAsync_WithGitHubService_UsesAuthenticatedGitHubService()
    {
        var gitHubService = Substitute.For<IGitHubService>();
        gitHubService.ListWorkflowRunArtifactsAsync("owner", "repo", 777, Arg.Any<CancellationToken>())
            .Returns(new List<(long Id, string Name, long SizeInBytes)> { (101, "rpms", 1024) });

        using var zipMs = new MemoryStream();
        using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("libtest-1.0.0.rpm");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("rpm-content");
        }
        zipMs.Position = 0;

        gitHubService.DownloadArtifactZipStreamAsync("owner", "repo", 101, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(new MemoryStream(zipMs.ToArray())));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var service = new ArtifactService(gitHubService, _httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var rpms = await service.DownloadRunRpmArtifactsAsync("owner", "repo", 777, tempDir);
            Assert.Single(rpms);
            Assert.EndsWith("libtest-1.0.0.rpm", rpms[0]);
            await gitHubService.Received(1).ListWorkflowRunArtifactsAsync("owner", "repo", 777, Arg.Any<CancellationToken>());
            await gitHubService.Received(1).DownloadArtifactZipStreamAsync("owner", "repo", 101, Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadRunRpmArtifactsAsync_Http403_LogsWarningNotDebug()
    {
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ArtifactService>>();
        var gitHubService = Substitute.For<IGitHubService>();
        gitHubService.ListWorkflowRunArtifactsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var service = new ArtifactService(gitHubService, _httpClientFactory, options, logger: logger);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var rpms = await service.DownloadRunRpmArtifactsAsync("owner", "repo", 888, tempDir);
            Assert.Empty(rpms);

            // Verify LogWarning was invoked (LogLevel.Warning)
            logger.Received().Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                Arg.Any<Microsoft.Extensions.Logging.EventId>(),
                Arg.Any<object>(),
                Arg.Any<Exception?>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}

