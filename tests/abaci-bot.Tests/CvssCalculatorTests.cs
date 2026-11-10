using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class CvssCalculatorTests
{
    [Theory]
    // Official FIRST CVSS v3.1 & v4.0 examples & standard vectors:
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", 9.8)]
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:C/C:L/I:L/A:N", 6.1)]
    [InlineData("CVSS:3.1/AV:L/AC:L/PR:N/UI:R/S:U/C:H/I:H/A:H", 7.8)]
    [InlineData("CVSS:3.1/AV:N/AC:H/PR:N/UI:N/S:U/C:L/I:N/A:N", 3.7)]
    [InlineData("CVSS:3.1/AV:P/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:N", 0.0)]
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:H/UI:N/S:C/C:H/I:H/A:H", 9.1)]
    [InlineData("CVSS:3.0/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", 9.8)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:H/SI:H/SA:H", 10.0)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N", 9.3)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:N/SC:N/SI:N/SA:N", 0.0)]
    [InlineData("CVSS:4.0/AV:A/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N", 8.7)]
    [InlineData("CVSS:4.0/AV:P/AC:H/AT:P/PR:H/UI:A/VC:L/VI:L/VA:L/SC:L/SI:L/SA:L", 4.8)]
    [InlineData("CVSS:4.0/AV:L/AC:L/AT:P/PR:L/UI:P/VC:H/VI:L/VA:N/SC:H/SI:N/SA:N", 8.3)]
    [InlineData("CVSS:4.0/AV:N/AC:H/AT:N/PR:N/UI:N/VC:N/VI:N/VA:H/SC:N/SI:H/SA:H", 9.4)]
    [InlineData("CVSS:4.0/AV:A/AC:L/AT:N/PR:L/UI:N/VC:L/VI:N/VA:N/SC:N/SI:N/SA:N", 6.0)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:N/SC:H/SI:N/SA:N", 7.4)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:N/SC:N/SI:H/SA:N", 7.4)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:N/SC:N/SI:N/SA:H", 7.4)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:N/SC:L/SI:N/SA:N", 5.6)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:N/SC:N/SI:L/SA:N", 5.6)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:N/SC:N/SI:N/SA:L", 5.6)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:N/VA:N/SC:N/SI:N/SA:N", 8.6)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:H/VA:N/SC:N/SI:N/SA:N", 8.6)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:H/SC:N/SI:N/SA:N", 8.6)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:L/VI:N/VA:N/SC:N/SI:N/SA:N", 6.9)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:L/VA:N/SC:N/SI:N/SA:N", 6.9)]
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:L/SC:N/SI:N/SA:N", 6.9)]
    [InlineData("CVSS:3.1/AV:A/AC:L/PR:L/UI:N/S:C/C:H/I:H/A:H", 9.0)]
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:H/UI:R/S:U/C:N/I:L/A:H", 5.2)]
    public void CalculateBaseScore_ValidVectors_ReturnsExactScore(string vector, double expectedScore)
    {
        var score = CvssCalculator.CalculateBaseScore(vector);
        Assert.NotNull(score);
        Assert.Equal(expectedScore, score.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOT_A_VECTOR")]
    [InlineData("CVSS:3.1/AV:N/AC:L")] // Incomplete
    [InlineData("CVSS:3.1/AV:X/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H")] // Invalid metric value
    [InlineData("CVSS:4.0/AV:N/AC:L")] // Incomplete CVSS 4.0
    [InlineData("CVSS:4.0/AV:X/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N")] // Invalid CVSS 4.0 metric
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VI:H/VA:H/SC:N/SI:N/SA:N")] // Missing VC
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SI:N/SA:N")] // Missing SC
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:X/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N")] // Invalid AT
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:X/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N")] // Invalid PR
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:X/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N")] // Invalid UI
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:X/VI:H/VA:H/SC:N/SI:N/SA:N")] // Invalid VC
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:X/VA:H/SC:N/SI:N/SA:N")] // Invalid VI
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:X/SC:N/SI:N/SA:N")] // Invalid VA
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:X/SI:N/SA:N")] // Invalid SC
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:X/SA:N")] // Invalid SI
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:X")] // Invalid SA
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:X/UI:N/S:U/C:H/I:H/A:H")] // Invalid PR in 3.1
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:X/S:U/C:H/I:H/A:H")] // Invalid UI in 3.1
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:X/C:H/I:H/A:H")] // Invalid S in 3.1
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:X/I:H/A:H")] // Invalid C in 3.1
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:X/A:H")] // Invalid I in 3.1
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:X")] // Invalid A in 3.1
    [InlineData("CVSS:2.0/AV:N/AC:L/Au:N/C:P/I:P/A:P")] // CVSS 2.0 not supported by v3 calculator
    public void CalculateBaseScore_InvalidOrIncompleteVectors_ReturnsNull(string? vector)
    {
        var score = CvssCalculator.CalculateBaseScore(vector);
        Assert.Null(score);
    }
}
