using abaci_bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace abaci_bot.Tests;

public class RepositoryAllowListTests
{
    [Fact]
    public void IsAllowed_NullOrEmptyAllowList_ReturnsFalse()
    {
        Assert.False(RepositoryAllowList.IsAllowed("openRuyi-Project", "openRuyi", null));
        Assert.False(RepositoryAllowList.IsAllowed("openRuyi-Project", "openRuyi", Array.Empty<string>()));
    }

    [Theory]
    [InlineData("openRuyi")]
    [InlineData("openRuyi-Project/openRuyi/extra")]
    [InlineData("/openRuyi")]
    [InlineData("openRuyi/")]
    [InlineData("owner with space/repo")]
    public void IsAllowed_MalformedOrBareRepoName_ReturnsFalse(string entry)
    {
        Assert.False(RepositoryAllowList.IsAllowed("openRuyi-Project", "openRuyi", new[] { entry }));
    }

    [Fact]
    public void IsAllowed_ValidOwnerRepo_CaseInsensitiveMatchReturnsTrue()
    {
        var list = new[] { " OpenRuyi-Project/OpenRuyi " };

        Assert.True(RepositoryAllowList.IsAllowed("openruyi-project", "openruyi", list));
        Assert.False(RepositoryAllowList.IsAllowed("other-owner", "openruyi", list));
        Assert.False(RepositoryAllowList.IsAllowed("", "openruyi", list));
    }

    [Fact]
    public void ParseRepositories_FiltersInvalidEntriesAndReturnsOwnerRepoPairs()
    {
        var list = new[] { "owner1/repo1", "bare-repo", "   ", "owner2/repo2/bad", " owner3 / repo3 " };
        var parsed = RepositoryAllowList.ParseRepositories(list, NullLogger.Instance);

        Assert.Equal(2, parsed.Count);
        Assert.Contains(("owner1", "repo1"), parsed);
        Assert.Contains(("owner3", "repo3"), parsed);
    }

    [Fact]
    public void FindInvalidEntries_ReturnsOnlyMalformedEntries()
    {
        Assert.Empty(RepositoryAllowList.FindInvalidEntries(null));
        Assert.Empty(RepositoryAllowList.FindInvalidEntries(Array.Empty<string>()));

        var list = new[] { "valid/repo", "bare-repo", "a/b/c", "", "bad owner/repo", null! };
        var invalid = RepositoryAllowList.FindInvalidEntries(list);

        Assert.Equal(5, invalid.Count);
        Assert.Contains("bare-repo", invalid);
        Assert.Contains("a/b/c", invalid);
        Assert.Contains("", invalid);
        Assert.Contains("bad owner/repo", invalid);
    }
}
