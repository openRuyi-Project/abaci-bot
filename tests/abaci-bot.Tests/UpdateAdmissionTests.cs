using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class UpdateAdmissionTests
{
    [Theory]
    [InlineData("20260526", "20260817", false)] // 8-digit CalVer same year
    [InlineData("20251231", "20260101", true)]  // 8-digit CalVer different year
    [InlineData("202602", "202608", false)]     // 6-digit CalVer same year (e.g. ovmf)
    [InlineData("202512", "202602", true)]      // 6-digit CalVer different year
    [InlineData("1.2.3", "1.3.0", false)]       // Standard SemVer minor update
    [InlineData("1.2.3", "2.0.0", true)]        // Standard SemVer major update
    [InlineData("v1.2.3", "v1.4.0", false)]     // Leading 'v' prefix
    [InlineData("0.4.88+curl-8.19.0", "0.4.89", false)] // Strips build suffix
    public void IsCrossMajorVersion_EvaluatesCorrectly(string current, string target, bool expected)
    {
        var result = UpdateAdmission.IsCrossMajorVersion(current, target);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("0.6.2", "0.6", true)]
    [InlineData("1.2.3", "1", true)]
    [InlineData("0.7.0", "0.6", false)]
    [InlineData("1.2.3", "", true)]
    public void IsVersionCompatibleWithBranch_EvaluatesBranchCompatibility(string target, string branch, bool expected)
    {
        var result = UpdateAdmission.IsVersionCompatibleWithBranch(target, branch);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsVersionAtTarget_DetectsMatchingVersion()
    {
        var spec = "Name: curl\nVersion: 8.2.1\nRelease: 1\n";
        Assert.True(UpdateAdmission.IsVersionAtTarget(spec, "8.2.1"));
        Assert.True(UpdateAdmission.IsVersionAtTarget(spec, "8.2.1+build1"));
        Assert.False(UpdateAdmission.IsVersionAtTarget(spec, "8.2.2"));
    }

    [Fact]
    public void IsVersionAtOrPastTarget_DetectsEqualOrHigherVersions()
    {
        var spec = "Name: curl\nVersion: 8.2.5\nRelease: 1\n";
        Assert.True(UpdateAdmission.IsVersionAtOrPastTarget(spec, "8.2.1"));
        Assert.True(UpdateAdmission.IsVersionAtOrPastTarget(spec, "8.2.5"));
        Assert.False(UpdateAdmission.IsVersionAtOrPastTarget(spec, "8.2.6"));
    }

    [Theory]
    [InlineData("1.2.0", "1.1.0", 1)]
    [InlineData("1.1.0", "1.2.0", -1)]
    [InlineData("1.0", "1.0", 0)]
    [InlineData("1.0~rc1", "1.0", -1)]
    [InlineData("1.0", "1.0~rc1", 1)]
    [InlineData("1.0^git1", "1.0", 1)]
    [InlineData("1.0", "1.0^git1", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.1+suffix", "1.0.1", 0)]
    [InlineData("1.0^git1", "1.0.1", -1)]
    [InlineData("1.0^", "1.0", 1)]
    [InlineData("1.0^", "1.0.1", -1)]
    [InlineData("1.0~rc1", "1.0~rc2", -1)]
    [InlineData("1.0", "1.0.", 0)]
    [InlineData("1.0a", "1.0.1", -1)]
    [InlineData("1.0a", "1.0", 1)]
    [InlineData("1.0", "1.0a", -1)]
    [InlineData("1.0", "1.0.0", -1)]
    [InlineData("01", "1", 0)]
    [InlineData("1.0.0", "1.0", 1)]
    [InlineData(null, null, 0)]
    [InlineData(null, "1.0", -1)]
    [InlineData("1.0", null, 1)]
    [InlineData("1.0~", "1.0~", 0)]
    [InlineData("1.0^", "1.0^", 0)]
    [InlineData("1.0~rc1", "1.0^git1", -1)]
    [InlineData("1.0^git1", "1.0~rc1", 1)]
    public void CompareRpmVersions_ComparesCorrectly(string? v1, string? v2, int expected)
    {
        var cmp = UpdateAdmission.CompareRpmVersions(v1, v2);
        Assert.Equal(expected, cmp);
    }

    [Fact]
    public void DeduceBranch_HandlesEdgeCases()
    {
        Assert.Equal(string.Empty, UpdateAdmission.DeduceBranch(null, "crate"));
        Assert.Equal(string.Empty, UpdateAdmission.DeduceBranch("rust-crate-0.5", null!));
        Assert.Equal(string.Empty, UpdateAdmission.DeduceBranch("other-dir", "crate"));
        Assert.Equal("0.5", UpdateAdmission.DeduceBranch("rust-rand-core-0.5", "rand_core"));
    }

    [Fact]
    public void StripVersionSuffix_HandlesEdgeCases()
    {
        Assert.Equal(string.Empty, UpdateAdmission.StripVersionSuffix(null!));
        Assert.Equal(string.Empty, UpdateAdmission.StripVersionSuffix("   "));
        Assert.Equal("1.2.3", UpdateAdmission.StripVersionSuffix("1.2.3"));
        Assert.Equal("1.2.3", UpdateAdmission.StripVersionSuffix("1.2.3+suffix"));
    }

    [Fact]
    public void IsVersionAtOrPastTarget_FullVersionAndEdgeCases()
    {
        Assert.False(UpdateAdmission.IsVersionAtOrPastTarget(null!, "1.0"));
        Assert.False(UpdateAdmission.IsVersionAtOrPastTarget("Version: 1.0", null!));
        Assert.False(UpdateAdmission.IsVersionAtOrPastTarget("No tags", "1.0"));

        var fullVerSpec = "%global full_version 2.5.0\nName: test\n";
        Assert.True(UpdateAdmission.IsVersionAtOrPastTarget(fullVerSpec, "2.4.0"));
        Assert.True(UpdateAdmission.IsVersionAtOrPastTarget(fullVerSpec, "2.5.0"));
        Assert.False(UpdateAdmission.IsVersionAtOrPastTarget(fullVerSpec, "2.6.0"));
    }

    [Fact]
    public void IsCrossMajorVersion_HandlesInvalidOrEmptyVersions()
    {
        Assert.False(UpdateAdmission.IsCrossMajorVersion("", "1.0"));
        Assert.False(UpdateAdmission.IsCrossMajorVersion("1.0", ""));
        Assert.False(UpdateAdmission.IsCrossMajorVersion("abc", "1.0"));
        Assert.False(UpdateAdmission.IsCrossMajorVersion("1.0", "abc"));
    }

    [Theory]
    [InlineData("curl", true)]
    [InlineData("gcc-14", true)]
    [InlineData("rust_core", true)]
    [InlineData("libfoo.bar", true)]
    [InlineData("../curl", false)]
    [InlineData("curl/malicious", false)]
    [InlineData("curl\\foo", false)]
    [InlineData("curl;rm -rf", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValidPackageName_ValidatesPackageNamesCorrectly(string name, bool expected)
    {
        Assert.Equal(expected, UpdateAdmission.IsValidPackageName(name));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("2026.05.26", true)]
    [InlineData("1.0_beta", true)]
    [InlineData("1.0+build1", true)]
    [InlineData("1.0~rc1", true)]
    [InlineData("2.0^git20261119", true)]
    [InlineData("1.2-3", false)]
    [InlineData("1.0\nName: hacked", false)]
    [InlineData("1.0\r\nName: hacked", false)]
    [InlineData("1.0/2", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValidTargetVersion_ValidatesTargetVersionsCorrectly(string version, bool expected)
    {
        Assert.Equal(expected, UpdateAdmission.IsValidTargetVersion(version));
    }

    [Theory]
    [InlineData("1.0", "1.0^git", -1)]
    [InlineData("1.0^git", "1.0", 1)]
    [InlineData("1.0^1", "1.0.1", -1)]
    [InlineData("1.0.1", "1.0^1", 1)]
    [InlineData("1.0.1", "1.0.a", 1)]
    [InlineData("1.0.a", "1.0.1", -1)]
    [InlineData("1.0.beta", "1.0.alpha", 1)]
    [InlineData("1.0.alpha", "1.0.beta", -1)]
    [InlineData("1.0.10", "1.0.9", 1)]
    [InlineData("1.0.9", "1.0.10", -1)]
    public void CompareRpmVersions_CaretAndSegmentComparisons(string v1, string v2, int expectedSign)
    {
        int actual = UpdateAdmission.CompareRpmVersions(v1, v2);
        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    [Fact]
    public void IsVersionCompatibleWithBranch_HandlesTrailingDotAndExactMatch()
    {
        Assert.True(UpdateAdmission.IsVersionCompatibleWithBranch("0.10.1", "0.10."));
        Assert.True(UpdateAdmission.IsVersionCompatibleWithBranch("0.10", "0.10"));
        Assert.True(UpdateAdmission.IsVersionCompatibleWithBranch("0.10.1", ""));
    }
}
