using System.Reflection;
using System.Text.RegularExpressions;
using abaci_bot.Options;
using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

/// <summary>
/// Keeps the human-facing documentation mechanically aligned with the code.
/// These tests only read repository files; they do not need a network or a running bot.
/// </summary>
public class DocumentationContractTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly Dictionary<Type, string> OptionDocs = new()
    {
        [typeof(TargetClassificationOptions)] = "docs/modules/target-classification.md",
        [typeof(ChangeTypeClassificationOptions)] = "docs/modules/change-type.md",
        [typeof(StaleLifecycleOptions)] = "docs/modules/stale-lifecycle.md",
        [typeof(SpdxComplianceOptions)] = "docs/modules/spdx-compliance.md",
        [typeof(CveSecurityOptions)] = "docs/modules/vulnerability.md",
        [typeof(RebuildImpactOptions)] = "docs/modules/rebuild-impact.md",
        [typeof(AbiComplianceOptions)] = "docs/modules/abi-compliance.md",
        [typeof(BotDetectionOptions)] = "docs/modules/workflow-state.md",
        [typeof(FileCacheOptions)] = "docs/configuration.md",
        [typeof(PackageUpdateOptions)] = "docs/modules/package-update.md",
        [typeof(CveActiveWatchOptions)] = "docs/modules/cve-active-watch.md"
    };

    private static readonly Dictionary<string, string> ModuleDocs = new()
    {
        ["WorkflowStateModule"] = "docs/modules/workflow-state.md",
        ["StaleUnstaleModule"] = "docs/modules/stale-lifecycle.md",
        ["ShinyNewPrModule"] = "docs/modules/workflow-state.md",
        ["UserContributionModule"] = "docs/modules/user-contribution.md",
        ["TargetClassificationModule"] = "docs/modules/target-classification.md",
        ["BuildSystemAnalysisModule"] = "docs/modules/build-system.md",
        ["ChangeTypeClassificationModule"] = "docs/modules/change-type.md",
        ["AiAssistanceModule"] = "docs/modules/ai-assistance.md",
        ["SpdxComplianceModule"] = "docs/modules/spdx-compliance.md",
        ["VulnerabilityModule"] = "docs/modules/vulnerability.md",
        ["RebuildImpactModule"] = "docs/modules/rebuild-impact.md",
        ["AbiComplianceModule"] = "docs/modules/abi-compliance.md"
    };

    [Fact]
    public void RelativeLinksInDocumentation_Resolve()
    {
        var documents = new List<string> { Path.Combine(RepoRoot, "README.md") };
        documents.AddRange(Directory.GetFiles(Path.Combine(RepoRoot, "docs"), "*.md", SearchOption.AllDirectories));

        var failures = new List<string>();
        foreach (var document in documents)
        {
            var text = File.ReadAllText(document);
            foreach (Match match in Regex.Matches(text, @"\[[^\]]*\]\(([^)]+)\)"))
            {
                var target = match.Groups[1].Value.Trim();
                if (target.StartsWith('#')
                    || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = target.Split('#', 2);
                var pathPart = parts[0];
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!, pathPart));
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    failures.Add($"{Path.GetRelativePath(RepoRoot, document)} -> {target}");
                    continue;
                }

                if (parts.Length == 2 && File.Exists(resolved) && resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ReadAnchors(resolved).Contains(parts[1], StringComparer.OrdinalIgnoreCase))
                    {
                        failures.Add($"{Path.GetRelativePath(RepoRoot, document)} -> {target} (missing anchor)");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, "Unresolved documentation links:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void CodeLinksToRepositoryDocs_Resolve()
    {
        var failures = new List<string>();
        var urlPattern = new Regex(
            @"https://github\.com/openRuyi-Project/abaci-bot/blob/master/(?<path>[^""\s#]+)(#(?<anchor>[^""\s]+))?");

        foreach (var file in Directory.GetFiles(RepoRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoRoot, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}.workbuddy{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in urlPattern.Matches(text))
            {
                var path = Path.Combine(RepoRoot, match.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    failures.Add($"{relative} -> {match.Value} (missing file)");
                    continue;
                }

                var anchor = match.Groups["anchor"].Value;
                if (!string.IsNullOrEmpty(anchor)
                    && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    && !ReadAnchors(path).Contains(anchor, StringComparer.OrdinalIgnoreCase))
                {
                    failures.Add($"{relative} -> {match.Value} (missing anchor)");
                }
            }
        }

        Assert.True(failures.Count == 0, "Code documentation links do not resolve:\n" + string.Join("\n", failures));
    }

    private static HashSet<string> ReadAnchors(string markdownPath)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(markdownPath))
        {
            var match = Regex.Match(line, @"^#{1,6}\s+(?<title>.+?)\s*$");
            if (!match.Success)
            {
                continue;
            }

            anchors.Add(Slugify(match.Groups["title"].Value));
        }

        return anchors;
    }

    private static string Slugify(string title)
    {
        var slug = title.ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9\s_-]", string.Empty);
        return slug.Replace(' ', '-');
    }

    [Fact]
    public void LabelProvisioningCommands_CoverEveryManagedLabel()
    {
        var labels = ManagedLabels();
        var docs = File.ReadAllText(Path.Combine(RepoRoot, "docs", "labels.md"));
        var commands = Regex.Matches(docs, @"gh label create ""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Type: Build Failure is a reserved constant and intentionally has no label.
        var expected = labels.Where(label => label != "Type: Build Failure").ToHashSet(StringComparer.Ordinal);

        Assert.True(
            expected.SetEquals(commands),
            "Provisioning commands and managed labels differ.\n" +
            $"Missing commands: {string.Join(", ", expected.Except(commands))}\n" +
            $"Unknown commands: {string.Join(", ", commands.Except(expected))}");
    }

    [Fact]
    public void EveryOptionProperty_IsMentionedInItsModuleDocumentation()
    {
        var failures = new List<string>();

        foreach (var (type, relativePath) in OptionDocs)
        {
            var path = Path.Combine(RepoRoot, relativePath);
            Assert.True(File.Exists(path), $"{relativePath} does not exist");
            var text = File.ReadAllText(path);

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.CanWrite)
                .Select(property => property.Name);

            failures.AddRange(properties
                .Where(property => !text.Contains(property, StringComparison.Ordinal))
                .Select(property => $"{type.Name}.{property} missing from {relativePath}"));
        }

        Assert.True(failures.Count == 0, "Options missing from documentation:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ModulePrioritiesAndRegistrations_MatchArchitectureTable()
    {
        var documented = new Dictionary<string, int>(StringComparer.Ordinal);
        var architecture = File.ReadAllText(Path.Combine(RepoRoot, "docs", "architecture.md"));
        foreach (Match match in Regex.Matches(architecture, @"\|\s*\*\*(\d+)\*\*\s*\|\s*`([A-Za-z]+Module)`"))
        {
            documented[match.Groups[2].Value] = int.Parse(match.Groups[1].Value);
        }

        var code = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, "Modules"), "*Module.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(file);
            var match = Regex.Match(text, @"public\s+int\s+Priority\s*=>\s*(\d+)\s*;");
            if (match.Success)
            {
                code[Path.GetFileNameWithoutExtension(file)] = int.Parse(match.Groups[1].Value);
            }
        }

        Assert.Equal(code, documented);

        var registered = Regex.Matches(
                File.ReadAllText(Path.Combine(RepoRoot, "Program.cs")),
                @"AddSingleton<IPullRequestModule,\s*(\w+)>")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            registered.SetEquals(code.Keys),
            $"Registered modules differ from prioritised modules.\nMissing: {string.Join(", ", registered.Except(code.Keys))}\nUnregistered: {string.Join(", ", code.Keys.Except(registered))}");
    }

    [Fact]
    public void BackgroundServices_AreDocumentedInArchitecture()
    {
        var architecture = File.ReadAllText(Path.Combine(RepoRoot, "docs", "architecture.md"));
        var hosted = Regex.Matches(
                File.ReadAllText(Path.Combine(RepoRoot, "Program.cs")),
                @"AddHostedService<(\w+)>")
            .Select(match => match.Groups[1].Value);

        foreach (var service in hosted)
        {
            Assert.Contains($"### {service}", architecture, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReadmeCapabilityTable_LinksEveryModulePage()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));
        var links = Regex.Matches(readme, @"\]\(([^)#]+)")
            .Select(match => match.Groups[1].Value.Trim())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (module, relativePath) in ModuleDocs)
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot, relativePath)), $"{module} points to missing {relativePath}");
            Assert.True(
                links.Contains(relativePath),
                $"README capability table does not link {relativePath} for {module}");
        }
    }

    [Fact]
    public void DemoConfigurationComments_StateDefaultsAndBehaviour()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, "appsettings.demo.jsonc"));
        var failures = new List<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            var match = Regex.Match(lines[index], @"^\s*""([^""]+)""\s*:\s*(.+)$");
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim();
            if (value.StartsWith('{'))
            {
                continue;
            }

            var comments = new List<string>();
            for (var cursor = index - 1; cursor >= 0 && lines[cursor].TrimStart().StartsWith("//"); cursor--)
            {
                comments.Add(lines[cursor].Trim().TrimStart('/').Trim());
            }

            var block = string.Join(" ", Enumerable.Reverse(comments));
            if (!block.Contains("Default:", StringComparison.Ordinal)
                && !block.Contains("Required:", StringComparison.Ordinal))
            {
                failures.Add($"{name}: comment must state Default or Required");
                continue;
            }

            if (Regex.IsMatch(value, @"^-?\d+(\.\d+)?$")
                && !block.Contains("Unit:", StringComparison.Ordinal)
                && !block.Contains("Range:", StringComparison.Ordinal))
            {
                failures.Add($"{name}: numeric setting must state Unit or Range");
            }

            if ((value == "true" || value == "false")
                && (!block.Contains("true:", StringComparison.Ordinal) || !block.Contains("false:", StringComparison.Ordinal)))
            {
                failures.Add($"{name}: boolean setting must state true and false behaviour");
            }
        }

        Assert.True(failures.Count == 0, "Configuration comment template violations:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void PullRequestTriggerLists_MatchModuleShouldProcess()
    {
        var failures = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, "Modules"), "*Module.cs", SearchOption.TopDirectoryOnly))
        {
            var module = Path.GetFileNameWithoutExtension(file);

            // Modules that share a documentation page or use a non-standard trigger layout
            // are covered by their own subsection, not the page-level `### Trigger Events`.
            if (module is "WorkflowStateModule" or "ShinyNewPrModule" or "StaleUnstaleModule"
                || !ModuleDocs.TryGetValue(module, out var relativePath))
            {
                continue;
            }

            var code = File.ReadAllText(file);
            var shouldProcess = Regex.Match(
                code,
                @"public\s+bool\s+ShouldProcess\([^)]*\)\s*\{(?<body>.*?)\n\s{4}\}",
                RegexOptions.Singleline);
            if (!shouldProcess.Success)
            {
                continue;
            }

            var codeActions = Regex.Matches(shouldProcess.Groups["body"].Value, @"PullRequestAction\.(\w+)")
                .Select(match => ToSnakeCase(match.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);
            if (codeActions.Count == 0)
            {
                continue;
            }

            var docsPath = Path.Combine(RepoRoot, relativePath);
            var docs = File.ReadAllText(docsPath);
            var trigger = Regex.Match(docs, @"### Trigger Events(?<body>.*?)(\n---|\n## )", RegexOptions.Singleline);
            if (!trigger.Success)
            {
                continue;
            }

            var list = Regex.Match(trigger.Groups["body"].Value, @"`pull_request`\s*\(([^)]*)\)");
            if (!list.Success)
            {
                continue;
            }

            var documented = Regex.Matches(list.Groups[1].Value, "`([^`]+)`")
                .Select(match => match.Groups[1].Value.Trim())
                .ToHashSet(StringComparer.Ordinal);

            if (!documented.SetEquals(codeActions))
            {
                failures.Add(
                    $"{relativePath}: documented [{string.Join(", ", documented.Order())}] != code [{string.Join(", ", codeActions.Order())}]");
            }
        }

        Assert.True(failures.Count == 0, "Trigger event drift:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ReferencedTestFiles_Exist()
    {
        var documents = new List<string> { Path.Combine(RepoRoot, "README.md") };
        documents.AddRange(Directory.GetFiles(Path.Combine(RepoRoot, "docs"), "*.md", SearchOption.AllDirectories));

        var existing = Directory.GetFiles(Path.Combine(RepoRoot, "tests"), "*Tests.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var document in documents)
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(document), @"([A-Za-z0-9_]+Tests\.cs)"))
            {
                var name = match.Groups[1].Value;
                if (!existing.Contains(name))
                {
                    missing.Add($"{Path.GetRelativePath(RepoRoot, document)} -> {name}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Documentation references missing test files:\n" + string.Join("\n", missing));
    }

    private static string ToSnakeCase(string value)
    {
        return Regex.Replace(value, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();
    }

    private static List<string> ManagedLabels()
    {
        return typeof(GitHubLabels)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }
}
