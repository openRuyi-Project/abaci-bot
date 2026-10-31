using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class GovernanceCommentFormatterTests
{
    private readonly GovernanceCommentFormatter _formatter = new();

    private static void AssertNoEmojis(string text)
    {
        // Common emojis used in previous iterations
        Assert.DoesNotContain("⚠️", text);
        Assert.DoesNotContain("🛡️", text);
        Assert.DoesNotContain("⛔", text);
        Assert.DoesNotContain("🚀", text);
    }

    private static void AssertNoInternalTaskCodes(string text)
    {
        Assert.DoesNotContain("Part 2.2", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Part2.2", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("W10", text);
        Assert.DoesNotContain("R12", text);
    }

    [Fact]
    public void FormatConsolidatedHeader_EnglishDefault_ReturnsExpectedHeaderWithoutEmojis()
    {
        var header = _formatter.FormatConsolidatedHeader();
        Assert.Equal("## abaci-bot Governance Review", header);
        AssertNoEmojis(header);
    }

    [Fact]
    public void FormatConsolidatedHeader_Chinese_ReturnsExpectedHeaderWithoutEmojis()
    {
        var header = _formatter.FormatConsolidatedHeader("zh-CN");
        Assert.Equal("## abaci-bot 门禁与治理检查报告", header);
        AssertNoEmojis(header);
    }

    [Fact]
    public void FormatSpdxLicenseIssue_EnglishDefault_UsesGfmWarning()
    {
        var issues = new List<(string File, string Expression, string Details)>
        {
            ("SPECS/Catch2/Catch2.spec", "Totally-Fake-License", "Non-standard or unknown identifier(s): `Totally-Fake-License`")
        };

        var output = _formatter.FormatSpdxLicenseIssue(issues);

        Assert.StartsWith("> [!WARNING]", output);
        Assert.Contains("SPDX License Compliance Check Failed", output);
        Assert.Contains("SPECS/Catch2/Catch2.spec", output);
        Assert.Contains("Totally-Fake-License", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }

    [Fact]
    public void FormatSpdxLicenseIssue_Chinese_UsesGfmWarning()
    {
        var issues = new List<(string File, string Expression, string Details)>
        {
            ("SPECS/test/test.spec", "FakeLicense", "非标准或未知标识符")
        };

        var output = _formatter.FormatSpdxLicenseIssue(issues, "zh-CN");

        Assert.StartsWith("> [!WARNING]", output);
        Assert.Contains("SPDX 许可证合规检查未通过", output);
        Assert.Contains("SPECS/test/test.spec", output);
        AssertNoEmojis(output);
    }

    [Fact]
    public void FormatCveBreakdown_EnglishDefault_UsesGfmNote()
    {
        var cves = new List<(string CveId, double? Cvss, string? Severity, string Source)>
        {
            ("CVE-2024-3094", 10.0, "Critical", "OSV"),
            ("CVE-2023-9999", null, "Unknown", "NVD")
        };

        var output = _formatter.FormatCveBreakdown(cves);

        Assert.StartsWith("> [!NOTE]", output);
        Assert.Contains("Security Vulnerability Assessment", output);
        Assert.Contains("CVE-2024-3094", output);
        Assert.Contains("10.0", output);
        Assert.Contains("No public CVSS score found", output);
        Assert.Contains("expected if embargoed or unreleased", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }

    [Fact]
    public void FormatRebuildGateBlocked_EnglishDefault_UsesGfmImportant()
    {
        var packages = new[] { "glibc", "openssl" };
        var output = _formatter.FormatRebuildGateBlocked(packages, 1500, 1001, "Rolling");

        Assert.StartsWith("> [!IMPORTANT]", output);
        Assert.Contains("Rebuild Impact Gate: Merge Blocked", output);
        Assert.Contains("1500", output);
        Assert.Contains("1001", output);
        Assert.Contains("Rolling", output);
        Assert.Contains("Staging", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }

    [Fact]
    public void FormatAbiBreakingChange_EnglishDefault_UsesGfmCautionAndTruncatesLongOutput()
    {
        var longSnippet = new string('A', 2000);
        var output = _formatter.FormatAbiBreakingChange(longSnippet);

        Assert.StartsWith("> [!CAUTION]", output);
        Assert.Contains("ABI Breaking Change Detected", output);
        Assert.Contains("(output truncated)", output);
        AssertNoEmojis(output);
        AssertNoInternalTaskCodes(output);
    }
}
