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
}
