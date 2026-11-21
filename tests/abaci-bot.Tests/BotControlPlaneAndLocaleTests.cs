using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Octokit;
using Octokit.Webhooks.Events.IssueComment;

namespace abaci_bot.Tests;

public class BotControlPlaneAndLocaleTests
{
    [Theory]
    [InlineData("@abaci-bot lock", "abaci-bot", "lock", "")]
    [InlineData("@ABACI-BOT: hide @spammer", "abaci-bot", "hide", "@spammer")]
    [InlineData("@abaci-bot[bot], ignore dnf5 broken upstream", "abaci-bot", "ignore", "dnf5 broken upstream")]
    [InlineData("  @aqua-rium status etcd", "aqua-rium", "status", "etcd")]
    [InlineData("@abaci-bot lang zh\r\nextra line", "abaci-bot", "lang", "zh")]
    public void BotCommandParser_TryParse_ValidMentionCommands(
        string body, string botLogin, string expectedVerb, string expectedArgs)
    {
        var ok = BotCommandParser.TryParse(body, botLogin, out var cmd);

        Assert.True(ok);
        Assert.Equal(expectedVerb, cmd.Verb);
        Assert.Equal(expectedArgs, cmd.Arguments);
    }

    [Theory]
    [InlineData("/lock", "abaci-bot")]
    [InlineData("@otherbot lock", "abaci-bot")]
    [InlineData("@abaci-bot", "abaci-bot")]
    [InlineData("@abaci-bot   ", "abaci-bot")]
    [InlineData("hello @abaci-bot lock", "abaci-bot")]
    [InlineData("", "abaci-bot")]
    public void BotCommandParser_TryParse_InvalidOrNonMention_ReturnsFalse(string body, string botLogin)
    {
        Assert.False(BotCommandParser.TryParse(body, botLogin, out _));
    }

    [Fact]
    public void JsonBotStateStore_PersistsIgnoresFailuresAndLocalesAcrossInstances()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-{Guid.NewGuid():N}");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            var store1 = new JsonBotStateStore(opts, NullLogger<JsonBotStateStore>.Instance);

            store1.SetIgnoredPackage("openRuyi-Project", "openRuyi", "dnf5", "upstream regression", "alice");
            store1.SetPackageFailure("openRuyi-Project", "openRuyi", "etcd", 3, DateTimeOffset.UtcNow.AddDays(30));
            store1.SetUserLocale("bob", "zh");

            var store2 = new JsonBotStateStore(opts, NullLogger<JsonBotStateStore>.Instance);
            var ignored = store2.GetIgnoredPackage("openRuyi-Project", "openRuyi", "dnf5");
            Assert.NotNull(ignored);
            Assert.Equal("upstream regression", ignored!.Reason);
            Assert.Equal("alice", ignored.IgnoredBy);

            var failure = store2.GetPackageFailure("openRuyi-Project", "openRuyi", "etcd");
            Assert.NotNull(failure);
            Assert.Equal(3, failure!.ConsecutiveFailures);
            Assert.NotNull(failure.CooldownUntil);

            Assert.Equal("zh", store2.GetUserLocale("bob"));

            Assert.True(store2.RemoveIgnoredPackage("openRuyi-Project", "openRuyi", "dnf5"));
            Assert.Null(store2.GetIgnoredPackage("openRuyi-Project", "openRuyi", "dnf5"));

            Assert.True(store2.ClearUserLocale("bob"));
            Assert.Null(store2.GetUserLocale("bob"));

            Assert.True(store2.ClearPackageFailure("openRuyi-Project", "openRuyi", "etcd"));
            Assert.Null(store2.GetPackageFailure("openRuyi-Project", "openRuyi", "etcd"));
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
    public void JsonBotStateStore_CorruptJson_DegradesGracefully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "bot-state.json"), "{ invalid json");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            var store = new JsonBotStateStore(opts, NullLogger<JsonBotStateStore>.Instance);
            Assert.Null(store.GetUserLocale("alice"));
            Assert.Empty(store.GetIgnoredPackages("owner", "repo"));
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
    public void LocaleResolver_EnforcesForcedVsDefaultRepositoryPolicies()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"locale-test-{Guid.NewGuid():N}");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                DefaultLocale = "en",
                RepositoryForcedLocales = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["org/forced-zh"] = "zh"
                },
                RepositoryDefaultLocales = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["org/default-zh"] = "zh"
                }
            });
            var store = new JsonBotStateStore(opts);
            store.SetUserLocale("alice", "en");
            store.SetUserLocale("bob", "zh");

            var resolver = new LocaleResolver(opts, store);

            // Forced repo disables user selection and overrides user preference
            Assert.False(resolver.IsUserSelectionAllowed("org", "forced-zh", out var forced));
            Assert.Equal("zh", forced);
            Assert.Equal("zh", resolver.ResolveForReply("org", "forced-zh", "alice"));

            // Repo default allows user selection; user preference wins over repo default
            Assert.True(resolver.IsUserSelectionAllowed("org", "default-zh", out _));
            Assert.Equal("en", resolver.ResolveForReply("org", "default-zh", "alice"));
            Assert.Equal("zh", resolver.ResolveForReply("org", "default-zh", "carol"));

            // Global fallback
            Assert.Equal("zh", resolver.ResolveForReply("org", "other", "bob"));
            Assert.Equal("en", resolver.ResolveForReply("org", "other", "carol"));

            // Bot PRs ignore user preferences and follow Forced -> RepoDefault -> GlobalDefault
            Assert.Equal("zh", resolver.ResolveForBotPullRequest("org", "forced-zh"));
            Assert.Equal("zh", resolver.ResolveForBotPullRequest("org", "default-zh"));
            Assert.Equal("en", resolver.ResolveForBotPullRequest("org", "other"));
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
    public async Task BotControlPlaneService_IgnoreUnignoreStatusAndLangFlow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-test-{Guid.NewGuid():N}");
        try
        {
            var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                DefaultLocale = "en",
                RepositoryForcedLocales = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["org/locked-lang"] = "en"
                }
            });
            var store = new JsonBotStateStore(stateOpts);
            var localeResolver = new LocaleResolver(stateOpts, store);
            var pkgOpts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());

            var gitHub = Substitute.For<IGitHubService>();
            var perm = Substitute.For<IRepositoryPermissionService>();
            perm.GetUserPermissionAsync("org", "repo", "maintainer", Arg.Any<CancellationToken>())
                .Returns(RepositoryAccessLevel.Maintain);
            perm.GetUserPermissionAsync("org", "repo", "outsider", Arg.Any<CancellationToken>())
                .Returns(RepositoryAccessLevel.Read);

            var specCatalog = Substitute.For<ISpecCatalog>();
            specCatalog.GetSpecAsync("dnf5", Arg.Any<CancellationToken>())
                .Returns(new SpecContext("dnf5", "SPECS/dnf5/dnf5.spec", "Name: dnf5\nVersion: 5.4.2\n", "sha"));

            var service = new BotControlPlaneService(gitHub, perm, store, localeResolver, pkgOpts, specCatalog);

            var context = new PullRequestContext(
                gitHubService: gitHub,
                teamName: "captains",
                headers: null,
                eventType: PullRequestEventType.IssueComment,
                owner: "org",
                repo: "repo",
                prNumber: 10,
                headSha: "sha",
                baseBranch: "main",
                isDraft: false,
                isMerged: false,
                title: "test",
                body: "@abaci-bot ignore dnf5 broken",
                senderLogin: "maintainer",
                authorLogin: "maintainer",
                action: "created",
                existingLabels: Array.Empty<string>(),
                commentAction: IssueCommentAction.Created);

            // 1. Non-maintainer cannot ignore
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("ignore", "dnf5"), "outsider");
            Assert.Null(store.GetIgnoredPackage("org", "repo", "dnf5"));

            // 2. Maintainer ignores dnf5
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("ignore", "dnf5 broken"), "maintainer");
            Assert.Equal("broken", store.GetIgnoredPackage("org", "repo", "dnf5")?.Reason);

            // 3. Status reports runtime ignore
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("status", "dnf5"), "outsider");
            await gitHub.Received().CreateCommentAsync(
                "org", "repo", 10,
                Arg.Is<string>(s => s.Contains("Runtime ignore", StringComparison.Ordinal) && s.Contains("broken", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());

            // 4. User switches language to zh
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("lang", "zh"), "outsider");
            Assert.Equal("zh", store.GetUserLocale("outsider"));

            // 5. Maintainer unignores dnf5
            await service.HandleCommandAsync(context, "abaci-bot", new ParsedBotCommand("unignore", "dnf5"), "maintainer");
            Assert.Null(store.GetIgnoredPackage("org", "repo", "dnf5"));

            // 6. Forced locale repo rejects lang command
            var forcedContext = new PullRequestContext(
                gitHubService: gitHub,
                teamName: "captains",
                headers: null,
                eventType: PullRequestEventType.IssueComment,
                owner: "org",
                repo: "locked-lang",
                prNumber: 11,
                headSha: "sha",
                baseBranch: "main",
                isDraft: false,
                isMerged: false,
                title: "test",
                body: "@abaci-bot lang zh",
                senderLogin: "outsider",
                authorLogin: "outsider",
                action: "created",
                existingLabels: Array.Empty<string>(),
                commentAction: IssueCommentAction.Created);

            await service.HandleCommandAsync(forcedContext, "abaci-bot", new ParsedBotCommand("lang", "zh"), "outsider");
            await gitHub.Received().CreateCommentAsync(
                "org", "locked-lang", 11,
                Arg.Is<string>(s => s.Contains("personal language selection is disabled", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<CancellationToken>());
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
    public void GeneratePullRequestBody_NoMixedChineseInEnglish_AndSupportsChineseLocale()
    {
        var candidate = new UpdateCandidate("dnf5", "5.4.2", "5.4.8", CandidateOrigin.Lookout, "https://lookout.openruyi.cn/packages/dnf5");

        var enBody = GitHubPackageUpdatePublisher.GeneratePullRequestBody(candidate, "en", "abaci-bot-test");
        Assert.DoesNotContain("如需停止", enBody, StringComparison.Ordinal);
        Assert.Contains("@abaci-bot-test ignore dnf5", enBody, StringComparison.Ordinal);
        Assert.Contains("#!NoAutoUpdate: <reason>", enBody, StringComparison.Ordinal);

        var zhBody = GitHubPackageUpdatePublisher.GeneratePullRequestBody(candidate, "zh", "abaci-bot-test");
        Assert.Contains("如需停止本包的自动更新", zhBody, StringComparison.Ordinal);
        Assert.Contains("@abaci-bot-test ignore dnf5", zhBody, StringComparison.Ordinal);
    }

    [Fact]
    public void BotStateOptionsValidator_RejectsInvalidValues_AndAcceptsValid()
    {
        var validator = new BotStateOptionsValidator();
        Assert.True(validator.Validate(null, new BotStateOptions()).Succeeded);

        var bad = new BotStateOptions
        {
            StateDirectory = "relative-state",
            LocaleRetentionDays = 0,
            DefaultLocale = "fr",
            RepositoryForcedLocales = new Dictionary<string, string> { ["badrepo"] = "de" }
        };
        var result = validator.Validate(null, bad);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("StateDirectory", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("LocaleRetentionDays", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("DefaultLocale", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("RepositoryForcedLocales", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildIdentity_ReturnsNonEmptyNonSensitiveMetadata()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.Version));
        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.Commit));
        Assert.NotEqual("unknown", BuildIdentity.Commit);
        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.ImageDigest));
        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.BuildTime));
        Assert.Contains(BuildIdentity.Deployment, new[] { "container", "systemd", "binary" });
    }

    [Fact]
    public void JsonBotStateStore_PrunesExpiredUserLocalesOnStartup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var stateJson = $$"""
            {
              "UserLocales": {
                "expiredUser": { "Locale": "zh", "UpdatedAt": "{{DateTimeOffset.UtcNow.AddDays(-200):O}}" },
                "activeUser": { "Locale": "zh", "UpdatedAt": "{{DateTimeOffset.UtcNow.AddDays(-10):O}}" }
              },
              "Repositories": {}
            }
            """;
        File.WriteAllText(Path.Combine(tempDir, "bot-state.json"), stateJson);
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                LocaleRetentionDays = 180
            });
            var store = new JsonBotStateStore(opts);
            Assert.Null(store.GetUserLocale("expiredUser"));
            Assert.Equal("zh", store.GetUserLocale("activeUser"));
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
    public async Task BotControlPlaneModule_RoutesOnlyControlPlaneCommands()
    {
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");
        var service = Substitute.For<IBotControlPlaneService>();
        var gitHub = Substitute.For<IGitHubService>();
        var module = new BotControlPlaneModule(identity, service, NullLogger<BotControlPlaneModule>.Instance);

        Assert.Equal(96, module.Priority);
        Assert.Equal("BotControlPlaneModule", module.ModuleName);

        PullRequestContext MakeCtx(string body, IssueCommentAction? action = null, PullRequestEventType evt = PullRequestEventType.IssueComment) =>
            new(gitHub, "captains", null, evt, "org", "repo", 1, "sha", "main", false, false, "t", body, "alice", "alice", "created", Array.Empty<string>(), commentAction: action ?? IssueCommentAction.Created);

        Assert.False(module.ShouldProcess(MakeCtx("hello")));
        Assert.False(module.ShouldProcess(MakeCtx("@abaci-bot status", IssueCommentAction.Edited)));
        Assert.False(module.ShouldProcess(MakeCtx("@abaci-bot status", evt: PullRequestEventType.PullRequest)));
        Assert.True(module.ShouldProcess(MakeCtx("@abaci-bot status")));

        // Ignores ConversationGuard verb and other bot
        await module.ProcessAsync(MakeCtx("@abaci-bot lock"), CancellationToken.None);
        await module.ProcessAsync(MakeCtx("@otherbot ignore dnf5"), CancellationToken.None);
        await service.DidNotReceiveWithAnyArgs().HandleCommandAsync(default!, default!, default!, default!, default);

        // Routes status
        var validCtx = MakeCtx("@abaci-bot status dnf5");
        await module.ProcessAsync(validCtx, CancellationToken.None);
        await service.Received(1).HandleCommandAsync(validCtx, "abaci-bot", new ParsedBotCommand("status", "dnf5"), "alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BotControlPlaneService_CoversStatusBranchInferenceLangResetAndCallerLocale()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-extra-{Guid.NewGuid():N}");
        try
        {
            var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            var store = new JsonBotStateStore(stateOpts);
            store.SetUserLocale("zh-caller", "zh");
            store.SetPackageFailure("org", "repo", "dnf5", 2, DateTimeOffset.UtcNow.AddDays(5));

            var localeResolver = new LocaleResolver(stateOpts, store);
            var pkgOpts = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
            var gitHub = Substitute.For<IGitHubService>();
            var perm = Substitute.For<IRepositoryPermissionService>();
            perm.GetUserPermissionAsync("org", "repo", "zh-caller", Arg.Any<CancellationToken>())
                .Returns(RepositoryAccessLevel.Read);

            var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
            typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(head, "autoupdate/dnf5");
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, head);
            gitHub.GetPullRequestAsync("org", "repo", 20, Arg.Any<CancellationToken>()).Returns(pr);

            var specCatalog = Substitute.For<ISpecCatalog>();
            specCatalog.GetSpecAsync("dnf5", Arg.Any<CancellationToken>())
                .Returns(new SpecContext("dnf5", "SPECS/dnf5/dnf5.spec", "#!NoAutoUpdate: hold\nName: dnf5\n", "sha"));

            var service = new BotControlPlaneService(gitHub, perm, store, localeResolver, pkgOpts, specCatalog);
            var ctx = new PullRequestContext(
                gitHub, "captains", null, PullRequestEventType.IssueComment, "org", "repo", 20, "sha", "main",
                false, false, "bump", "@abaci-bot status", "zh-caller", "en-author", "created", Array.Empty<string>(),
                commentAction: IssueCommentAction.Created);

            // 1. Status without args infers dnf5 from autoupdate/dnf5 and replies in caller's zh locale
            await service.HandleCommandAsync(ctx, "abaci-bot", new ParsedBotCommand("status", ""), "zh-caller");
            await gitHub.Received().CreateCommentAsync(
                "org", "repo", 20,
                Arg.Is<string>(s => s.Contains("软件包 `dnf5` 自动更新状态", StringComparison.Ordinal) && s.Contains("连续失败 2 次", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());

            // 2. Invalid lang usage & lang reset
            await service.HandleCommandAsync(ctx, "abaci-bot", new ParsedBotCommand("lang", "fr"), "zh-caller");
            await service.HandleCommandAsync(ctx, "abaci-bot", new ParsedBotCommand("lang", "reset"), "zh-caller");
            Assert.Null(store.GetUserLocale("zh-caller"));

            // 3. Non-maintainer unignore denied
            await service.HandleCommandAsync(ctx, "abaci-bot", new ParsedBotCommand("unignore", "dnf5"), "zh-caller");
            await gitHub.Received().CreateCommentAsync(
                "org", "repo", 20,
                Arg.Is<string>(s => s.Contains("Only repository maintainers", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
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
    public void JsonBotStateStore_UnwritableStateDirectory_FailsFast()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), $"bot-state-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blockingFile, "not a directory");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = Path.Combine(blockingFile, "nested")
            });

            var ex = Assert.Throws<InvalidOperationException>(() => new JsonBotStateStore(opts));
            Assert.Contains("is not writable", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(blockingFile))
            {
                File.Delete(blockingFile);
            }
        }
    }

    [Fact]
    public async Task ManualGuideRebuildVerificationOrchestrator_HonorsRequestedLocale()
    {
        var formatter = new GovernanceCommentFormatter();
        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions());
        var orchestrator = new ManualGuideRebuildVerificationOrchestrator(formatter, options);

        var request = new RebuildVerificationRequest(
            "org", "repo", 99, "sha", "main",
            new[] { "openssl" }, 1500, 1001, Locale: "zh");

        var result = await orchestrator.RequestVerificationAsync(request);
        Assert.Equal(RebuildVerificationState.ManualVerificationRequired, result.State);
        Assert.Contains("Rebuild 规模准入拦截", result.AdvisoryComment, StringComparison.Ordinal);
    }

    [Fact]
    public void BotStatePathResolver_AlwaysReturnsRootedAbsoluteDirectory()
    {
        var defaultDir = BotStatePathResolver.ResolveStateDirectory(null);
        Assert.True(Path.IsPathRooted(defaultDir));
        Assert.False(string.Equals(defaultDir, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

        var explicitRelative = BotStatePathResolver.ResolveStateDirectory("relative-state");
        Assert.True(Path.IsPathRooted(explicitRelative));
    }

    [Theory]
    [InlineData("1.0.0+0a5160cd06da1234", null, false, "0a5160cd06da")]
    [InlineData("1.0.0+0a5160cd06da1234-dirty", null, false, "0a5160cd06da-dirty")]
    [InlineData("1.0.0+unknown", "0a5160cd06da1234", false, "0a5160cd06da")]
    [InlineData("1.0.0", "0a5160cd06da1234", true, "0a5160cd06da-dirty")]
    [InlineData("1.0.0", "0a5160cd06da-dirty", false, "0a5160cd06da-dirty")]
    [InlineData("1.0.0", null, true, "unknown")]
    public void BuildIdentity_ResolveCommitFrom_HandlesCleanDirtyAndFallback(
        string? informationalVersion, string? envCommit, bool envDirty, string expected)
    {
        Assert.Equal(expected, BuildIdentity.ResolveCommitFrom(informationalVersion, envCommit, envDirty));
    }

    [Fact]
    public void UpdateAdmission_SupportsRpmEpochInValidationAndComparison()
    {
        Assert.True(UpdateAdmission.IsValidTargetVersion("1:2.0"));
        Assert.True(UpdateAdmission.IsValidTargetVersion("2:1.2.3~rc1"));
        Assert.False(UpdateAdmission.IsValidTargetVersion("abc:2.0"));

        Assert.True(UpdateAdmission.CompareRpmVersions("1:1.0", "2.0") > 0);
        Assert.True(UpdateAdmission.CompareRpmVersions("2.0", "1:1.0") < 0);
        Assert.Equal(0, UpdateAdmission.CompareRpmVersions("1:2.0", "1:2.0"));
        Assert.True(UpdateAdmission.CompareRpmVersions("2:1.0", "1:9.9") > 0);
    }

    [Fact]
    public void JsonBotStateStore_PersistsRecordedFailedPrDeduplication()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bot-state-failedpr-{Guid.NewGuid():N}");
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions { StateDirectory = tempDir });
            var store1 = new JsonBotStateStore(opts);
            Assert.True(store1.TryMarkFailedPrRecorded("org", "repo", 101));
            Assert.False(store1.TryMarkFailedPrRecorded("org", "repo", 101));

            var store2 = new JsonBotStateStore(opts);
            Assert.False(store2.TryMarkFailedPrRecorded("org", "repo", 101));
            Assert.True(store2.TryMarkFailedPrRecorded("org", "repo", 102));
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
    public async Task EphemeralReplyService_PersistsAcrossRestart_DeletesById_AndRetriesTransientFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ephemeral-test-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var gitHub = Substitute.For<IGitHubService>();
        var gateway = Substitute.For<IEphemeralCommentGateway>();
        gateway.CreateCommentWithMetadataAsync("org", "repo", 42, "Usage hint", Arg.Any<CancellationToken>())
            .Returns(new CreatedCommentRef(501L, "IC_kwDO501"));

        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                EphemeralReplyDeleteMinutes = 5
            });
            var store1 = new JsonBotStateStore(opts);
            var service1 = new EphemeralReplyService(gitHub, store1, opts, gateway, NullLogger<EphemeralReplyService>.Instance, timeProvider);

            await service1.PostEphemeralReplyAsync("org", "repo", 42, "Usage hint");
            Assert.Equal(0, await service1.ProcessDueRepliesAsync());

            // Simulate process restart with new store and service instance
            var store2 = new JsonBotStateStore(opts);
            var service2 = new EphemeralReplyService(gitHub, store2, opts, gateway, NullLogger<EphemeralReplyService>.Instance, timeProvider);

            // Advance 6 min -> transient failure on first delete schedules retry (+1m)
            timeProvider.Advance(TimeSpan.FromMinutes(6));
            gateway.DeleteCommentAsync("org", "repo", 501L, Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new HttpRequestException("transient"));

            Assert.Equal(0, await service2.ProcessDueRepliesAsync());
            Assert.Empty(store2.GetDueEphemeralComments(timeProvider.GetUtcNow()));

            // Advance 1 min -> retry succeeds and removes entry from store
            timeProvider.Advance(TimeSpan.FromMinutes(1));
            gateway.DeleteCommentAsync("org", "repo", 501L, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Assert.Equal(1, await service2.ProcessDueRepliesAsync());
            await gateway.Received(2).DeleteCommentAsync("org", "repo", 501L, Arg.Any<CancellationToken>());
            Assert.Empty(store2.GetDueEphemeralComments(timeProvider.GetUtcNow().AddHours(1)));
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
    public async Task ConversationGuardService_AuthorConfirmationPrompt_UsesConfirmTimeoutInsteadOf5MinDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cg-ttl-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var gitHub = Substitute.For<IGitHubService>();
        var gateway = Substitute.For<IEphemeralCommentGateway>();
        gateway.CreateCommentWithMetadataAsync("owner", "repo", 42, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CreatedCommentRef(777L, "IC_kwDO777"));
        var perm = Substitute.For<IRepositoryPermissionService>();
        perm.GetUserPermissionAsync("owner", "repo", "author-user", Arg.Any<CancellationToken>())
            .Returns(RepositoryAccessLevel.Read);
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");

        try
        {
            var stateOpts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                EphemeralReplyDeleteMinutes = 5
            });
            var store = new JsonBotStateStore(stateOpts);
            var ephemeral = new EphemeralReplyService(gitHub, store, stateOpts, gateway, timeProvider: timeProvider);

            var cgOpts = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
            {
                Enabled = true,
                DryRun = false,
                AuthorLockConfirmTimeoutMinutes = 15
            });
            var service = new ConversationGuardService(
                gitHub, perm, cgOpts, NullLogger<ConversationGuardService>.Instance, timeProvider,
                botIdentity: identity, ephemeralReplies: ephemeral);

            var ctx = new PullRequestContext(
                gitHub, "captains", null, PullRequestEventType.IssueComment, "owner", "repo", 42, "sha", "main",
                false, false, "Title", "@abaci-bot lock", "author-user", "author-user", "created", Array.Empty<string>());

            await service.HandleCommandAsync(ctx, "@abaci-bot lock", "author-user");

            // At +6 minutes (past default 5m), confirmation prompt must still be alive
            timeProvider.Advance(TimeSpan.FromMinutes(6));
            Assert.Equal(0, await ephemeral.ProcessDueRepliesAsync());

            // At +15 minutes, confirmation prompt is deleted
            timeProvider.Advance(TimeSpan.FromMinutes(9));
            Assert.Equal(1, await ephemeral.ProcessDueRepliesAsync());
            await gateway.Received(1).DeleteCommentAsync("owner", "repo", 777L, Arg.Any<CancellationToken>());
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
    public async Task EphemeralReplyService_ActionMinimize_PersistsNodeIdAcrossRestart_AndRetriesWhenFalse()
    {
        const long largeCommentId = 7_059_426_126L;
        const string nodeId = "IC_kwDO_601";
        var tempDir = Path.Combine(Path.GetTempPath(), $"ephemeral-min-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var gitHub = Substitute.For<IGitHubService>();
        var gateway = Substitute.For<IEphemeralCommentGateway>();
        gateway.CreateCommentWithMetadataAsync("org", "repo", 42, "Usage hint", Arg.Any<CancellationToken>())
            .Returns(new CreatedCommentRef(largeCommentId, nodeId));

        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(new BotStateOptions
            {
                StateDirectory = tempDir,
                EphemeralReplyDeleteMinutes = 5,
                EphemeralReplyAction = EphemeralReplyAction.Minimize
            });
            var store1 = new JsonBotStateStore(opts);
            var service1 = new EphemeralReplyService(gitHub, store1, opts, gateway, timeProvider: timeProvider);

            await service1.PostEphemeralReplyAsync("org", "repo", 42, "Usage hint");

            // Simulate process restart with new store and service instance
            var store2 = new JsonBotStateStore(opts);
            var service2 = new EphemeralReplyService(gitHub, store2, opts, gateway, timeProvider: timeProvider);

            // First attempt returns false -> must NOT remove pending entry, must schedule retry (+1m)
            timeProvider.Advance(TimeSpan.FromMinutes(6));
            gateway.MinimizeCommentAsync("org", "repo", largeCommentId, nodeId, "OUTDATED", Arg.Any<CancellationToken>())
                .Returns(false);

            Assert.Equal(0, await service2.ProcessDueRepliesAsync());
            Assert.Empty(store2.GetDueEphemeralComments(timeProvider.GetUtcNow()));
            Assert.Single(store2.GetDueEphemeralComments(timeProvider.GetUtcNow().AddMinutes(1)));

            // Retry returns true -> clears pending entry using persisted NodeId
            timeProvider.Advance(TimeSpan.FromMinutes(1));
            gateway.MinimizeCommentAsync("org", "repo", largeCommentId, nodeId, "OUTDATED", Arg.Any<CancellationToken>())
                .Returns(true);

            Assert.Equal(1, await service2.ProcessDueRepliesAsync());
            await gateway.Received(2).MinimizeCommentAsync("org", "repo", largeCommentId, nodeId, "OUTDATED", Arg.Any<CancellationToken>());
            await gateway.DidNotReceive().DeleteCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
            Assert.Empty(store2.GetDueEphemeralComments(timeProvider.GetUtcNow().AddHours(1)));
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
    public void BotStateOptionsValidator_RejectsInvalidEphemeralReplyAction()
    {
        var validator = new BotStateOptionsValidator();
        Assert.True(validator.Validate(null, new BotStateOptions { EphemeralReplyAction = EphemeralReplyAction.Minimize }).Succeeded);

        var bad = new BotStateOptions { EphemeralReplyAction = (EphemeralReplyAction)99 };
        var result = validator.Validate(null, bad);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("EphemeralReplyAction", StringComparison.Ordinal));
    }

    [Fact]
    public void HealthResponseBuilder_SeparatesDefaultLivenessFromVerboseDiagnostics_AndTracksStartupUptimeAndLoggerErrors()
    {
        var initialTime = new DateTimeOffset(2026, 11, 21, 7, 41, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(initialTime);
        var runtimeHealth = new RuntimeHealthState(timeProvider);

        // Advance 45 seconds BEFORE first health request
        timeProvider.Advance(TimeSpan.FromSeconds(45));

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RuntimeErrorLoggerProvider(runtimeHealth));
        });
        var logger = loggerFactory.CreateLogger("abaci_bot.Services.TestService");

        // Warnings must NOT populate lastError
        logger.LogWarning("Transient warning that should not set lastError");

        var minimalJson = System.Text.Json.JsonSerializer.Serialize(HealthResponseBuilder.Build(
            verbose: false,
            runtimeHealth,
            new PackageUpdateOptions(),
            new CveActiveWatchOptions(),
            new MilestoneSyncOptions(),
            new StaleLifecycleOptions(),
            new ConversationGuardOptions(),
            new AbiComplianceOptions(),
            new RebuildImpactOptions(),
            new SpdxComplianceOptions(),
            new CveSecurityOptions()));

        Assert.Contains("\"status\":\"healthy\"", minimalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("abiEngine", minimalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("conversationGuardPermission", minimalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("uptimeSeconds", minimalJson, StringComparison.Ordinal);

        var firstVerboseJson = System.Text.Json.JsonSerializer.Serialize(HealthResponseBuilder.Build(
            verbose: true,
            runtimeHealth,
            new PackageUpdateOptions { Enabled = true },
            new CveActiveWatchOptions(),
            new MilestoneSyncOptions(),
            new StaleLifecycleOptions(),
            new ConversationGuardOptions(),
            new AbiComplianceOptions(),
            new RebuildImpactOptions(),
            new SpdxComplianceOptions(),
            new CveSecurityOptions()));

        Assert.Contains("\"uptimeSeconds\":45", firstVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"lastError\":null", firstVerboseJson, StringComparison.Ordinal);

        // Error logged via host ILogger populates lastError and redacts tokens
        logger.LogError(new InvalidOperationException("bad token ghp_secret123"), "Sweep failed");

        var secondVerboseJson = System.Text.Json.JsonSerializer.Serialize(HealthResponseBuilder.Build(
            verbose: true,
            runtimeHealth,
            new PackageUpdateOptions { Enabled = true },
            new CveActiveWatchOptions(),
            new MilestoneSyncOptions(),
            new StaleLifecycleOptions(),
            new ConversationGuardOptions(),
            new AbiComplianceOptions(),
            new RebuildImpactOptions(),
            new SpdxComplianceOptions(),
            new CveSecurityOptions()));

        Assert.Contains("\"capabilities\"", secondVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"abiEngine\"", secondVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"conversationGuardPermission\"", secondVerboseJson, StringComparison.Ordinal);
        Assert.Contains("\"modules\"", secondVerboseJson, StringComparison.Ordinal);
        Assert.Contains("TestService: Sweep failed (InvalidOperationException: bad token [REDACTED])", secondVerboseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_secret123", secondVerboseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationGuardService_ConfirmWithTrailingColon_Succeeds()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var perm = Substitute.For<IRepositoryPermissionService>();
        perm.GetUserPermissionAsync("owner", "repo", "author-user", Arg.Any<CancellationToken>())
            .Returns(RepositoryAccessLevel.Read);
        var identity = Substitute.For<IBotIdentityProvider>();
        identity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");

        var opts = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions { Enabled = true, DryRun = false });
        var service = new ConversationGuardService(gitHub, perm, opts, NullLogger<ConversationGuardService>.Instance, botIdentity: identity);

        var ctx = new PullRequestContext(
            gitHub, "captains", null, PullRequestEventType.IssueComment, "owner", "repo", 42, "sha", "main",
            false, false, "Title", "@abaci-bot lock", "author-user", "author-user", "created", Array.Empty<string>());

        await service.HandleCommandAsync(ctx, "@abaci-bot lock", "author-user");
        await service.HandleCommandAsync(ctx, "@abaci-bot lock confirm:", "author-user");

        await gitHub.Received(1).LockIssueAsync("owner", "repo", 42, LockReason.TooHeated, Arg.Any<CancellationToken>());
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public MutableTimeProvider(DateTimeOffset initial) => _utcNow = initial;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
