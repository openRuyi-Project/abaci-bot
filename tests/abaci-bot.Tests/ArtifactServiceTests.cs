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
    public async Task DownloadAndExtractRpmArtifactAsync_PreservesDebuginfoRpmsForPairing()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var binaryEntry = archive.CreateEntry("pcre2-10.49-1.oe2403.x86_64.rpm");
            using (var writer = new StreamWriter(binaryEntry.Open()))
            {
                writer.Write("binary-rpm");
            }

            var debugEntry = archive.CreateEntry("pcre2-debuginfo-10.49-1.oe2403.x86_64.rpm");
            using (var writer = new StreamWriter(debugEntry.Open()))
            {
                writer.Write("debuginfo-rpm");
            }

            var srcEntry = archive.CreateEntry("pcre2-10.49-1.oe2403.src.rpm");
            using (var writer = new StreamWriter(srcEntry.Open()))
            {
                writer.Write("src-rpm");
            }

            var debugsourceEntry = archive.CreateEntry("pcre2-debugsource-10.49-1.oe2403.x86_64.rpm");
            using (var writer = new StreamWriter(debugsourceEntry.Open()))
            {
                writer.Write("debugsource-rpm");
            }
        }
        ms.Position = 0;

        var gitHubService = Substitute.For<IGitHubService>();
        gitHubService.DownloadArtifactZipStreamAsync("owner", "repo", 456, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(ms.ToArray())));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var service = new ArtifactService(gitHubService, _httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var extracted = await service.DownloadAndExtractRpmArtifactAsync("owner", "repo", 456, tempDir);

            // Both binary and debuginfo should be extracted (debugsource and src are skipped)
            Assert.Equal(2, extracted.Count);
            Assert.Contains(extracted, p => p.EndsWith("pcre2-10.49-1.oe2403.x86_64.rpm"));
            Assert.Contains(extracted, p => p.EndsWith("pcre2-debuginfo-10.49-1.oe2403.x86_64.rpm"));

            // Pairing verification
            var binaryPath = extracted.First(p => p.EndsWith("pcre2-10.49-1.oe2403.x86_64.rpm"));
            var matchedDebug = ArtifactService.FindMatchingDebuginfoRpm(binaryPath, extracted);
            Assert.NotNull(matchedDebug);
            Assert.EndsWith("pcre2-debuginfo-10.49-1.oe2403.x86_64.rpm", matchedDebug);
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
    public void FindMatchingDebuginfoRpm_EmptyOrNoMatch_ReturnsNull()
    {
        Assert.Null(ArtifactService.FindMatchingDebuginfoRpm(string.Empty, new[] { "foo-debuginfo-1.0.rpm" }));
        Assert.Null(ArtifactService.FindMatchingDebuginfoRpm("glibc-2.38-1.x86_64.rpm", new[] { "pcre2-debuginfo-10.49-1.x86_64.rpm", "zstd-debuginfo-1.5.0.rpm" }));
    }

    [Fact]
    public void FindMatchingDebuginfoRpm_SingleUnrelatedDebuginfo_DoesNotMisattributeAndReturnsNull()
    {
        // Must never misattribute unrelated single debuginfo in multi-package workflows
        var singleCandidate = new[] { "pcre2-debuginfo-10.49-1.x86_64.rpm" };
        Assert.Null(ArtifactService.FindMatchingDebuginfoRpm("glibc-2.38-1.x86_64.rpm", singleCandidate));
        Assert.Null(ArtifactService.FindMatchingDebuginfoRpm("foo-1.0-1.x86_64.rpm", new[] { "bar-debuginfo-1.0-1.x86_64.rpm" }));
    }

    [Fact]
    public async Task DownloadBaselineRpmAsync_WithLoadedMetadata_ResolvesNevraLocationHref()
    {
        string? requestedUrl = null;
        var handler = new DelegatingHandlerStub(req =>
        {
            requestedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("nevra-rpm-bytes"))
            };
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("StableRpmClient").Returns(new HttpClient(handler));

        var rpmDependencyService = Substitute.For<IRpmDependencyService>();
        rpmDependencyService.IsMetadataLoaded.Returns(true);
        rpmDependencyService.GetPackageLocation("pcre2").Returns("Packages/p/pcre2-10.42-1.oe2403.x86_64.rpm");

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            StableRpmBaseUrl = "https://mirror.example.com/openEuler"
        });
        var service = new ArtifactService(_gitHub, clientFactory, options, rpmDependencyService: rpmDependencyService);
        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_nevra_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await service.DownloadBaselineRpmAsync("pcre2", tempDir);
            Assert.NotNull(result);
            Assert.Equal("https://mirror.example.com/openEuler/Packages/p/pcre2-10.42-1.oe2403.x86_64.rpm", requestedUrl);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FindMatchingDebuginfoRpm_Subpackage_MatchesSourceDebuginfoPackage()
    {
        var extracted = new[]
        {
            "/tmp/pcre2-10.49-1.oe2403.x86_64.rpm",
            "/tmp/pcre2-tools-10.49-1.oe2403.x86_64.rpm",
            "/tmp/pcre2-debuginfo-10.49-1.oe2403.x86_64.rpm"
        };
        var matched = ArtifactService.FindMatchingDebuginfoRpm("/tmp/pcre2-tools-10.49-1.oe2403.x86_64.rpm", extracted);
        Assert.NotNull(matched);
        Assert.EndsWith("pcre2-debuginfo-10.49-1.oe2403.x86_64.rpm", matched);
    }

    [Fact]
    public async Task DownloadBaselineDebuginfoRpmAsync_PackageAlreadyEndsWithDebuginfo_DoesNotDuplicateSuffix()
    {
        string? requestedUrl = null;
        var handler = new DelegatingHandlerStub(req =>
        {
            requestedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("debug-bytes"))
            };
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("StableRpmClient").Returns(new HttpClient(handler));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            StableRpmBaseUrl = "https://mirror.example.com/repo"
        });
        var service = new ArtifactService(_gitHub, clientFactory, options);
        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_debuginfo_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await service.DownloadBaselineDebuginfoRpmAsync("pcre2-debuginfo", tempDir);
            Assert.NotNull(result);
            Assert.NotNull(requestedUrl);
            Assert.DoesNotContain("debuginfo-debuginfo", requestedUrl);
            Assert.Contains("pcre2-debuginfo.rpm", requestedUrl);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadBaselineDebuginfoRpmAsync_WithValidConfig_DownloadsDebuginfoPackage()
    {
        string? requestedUrl = null;
        var handler = new DelegatingHandlerStub(req =>
        {
            requestedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("debuginfo-rpm-bytes"))
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
            var downloaded = await service.DownloadBaselineDebuginfoRpmAsync("pcre2", tempDir);
            Assert.NotNull(downloaded);
            Assert.True(File.Exists(downloaded));
            Assert.Equal("https://repo.openeuler.org/baselines/pcre2-debuginfo.rpm", requestedUrl);
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
    public async Task DownloadBaselineRpmAsync_BaseUrlEndsWithPackages_DoesNotDuplicatePackagesPath()
    {
        var requestedUrls = new List<string>();
        var handler = new DelegatingHandlerStub(req =>
        {
            var url = req.RequestUri!.ToString();
            requestedUrls.Add(url);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler);
        _httpClientFactory.CreateClient("StableRpmClient").Returns(client);

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            StableRpmBaseUrl = "https://repo.openeuler.org/OS/x86_64/Packages"
        });

        var service = new ArtifactService(_gitHub, _httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var downloaded = await service.DownloadBaselineRpmAsync("libgpg-error", tempDir);
            Assert.Null(downloaded);
            // Must attempt only once, not repeating /Packages/Packages/
            Assert.Single(requestedUrls);
            Assert.Equal("https://repo.openeuler.org/OS/x86_64/Packages/libgpg-error.rpm", requestedUrls[0]);
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
    [InlineData("Catch2-3.14.0-999.or.x86_64.rpm", true)]
    [InlineData("libdemo-1.0.0-1.el9.aarch64.rpm", true)]
    [InlineData("Catch2-3.14.0-999.or.src.rpm", false)]
    [InlineData("Catch2-3.14.0-999.or.nosrc.rpm", false)]
    [InlineData("Catch2-debuginfo-3.14.0-999.or.x86_64.rpm", false)]
    [InlineData("Catch2-debugsource-3.14.0-999.or.x86_64.rpm", false)]
    [InlineData("readme.txt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEligibleBinaryRpm_CorrectlyDifferentiatesBinaryAndAuxiliaryPackages(string? fileName, bool expected)
    {
        var actual = ArtifactService.IsEligibleBinaryRpm(fileName!);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Catch2-3.14.0-999.or.src.rpm", "Catch2")]
    [InlineData("Catch2-3.14.0-999.or.x86_64.rpm", "Catch2")]
    [InlineData("Catch2-debuginfo-3.14.0-999.or.x86_64.rpm", "Catch2-debuginfo")]
    [InlineData("Catch2-devel-3.14.0-999.or.x86_64.rpm", "Catch2-devel")]
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
    public async Task DownloadBaselineRpmAsync_InitialUrl404_RetriesPackagesSubdir()
    {
        var requestedUrls = new List<string>();
        var handler = new DelegatingHandlerStub(req =>
        {
            var url = req.RequestUri!.ToString();
            requestedUrls.Add(url);
            if (url.EndsWith("/Packages/libgpg-error.rpm", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("rpm-packages-subdir-bytes"))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
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
            Assert.Equal(2, requestedUrls.Count);
            Assert.Equal("https://repo.openeuler.org/baselines/libgpg-error.rpm", requestedUrls[0]);
            Assert.Equal("https://repo.openeuler.org/baselines/Packages/libgpg-error.rpm", requestedUrls[1]);
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

    [Fact]
    public async Task DownloadAndExtractRpmArtifactAsync_ExcessiveZipEntries_ThrowsInvalidDataExceptionToPreventZipBomb()
    {
        // Construct a zip file containing 1005 entries
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < 1005; i++)
            {
                var entry = archive.CreateEntry($"pkg-{i}.rpm");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("rpm-data");
            }
        }
        ms.Position = 0;

        _gitHub.DownloadArtifactZipStreamAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(ms.ToArray()));

        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var service = new ArtifactService(_gitHub, _httpClientFactory, options);

        var tempDir = Path.Combine(Path.GetTempPath(), "abaci_zipbomb_" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadAndExtractRpmArtifactAsync("owner", "repo", 999, tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}

