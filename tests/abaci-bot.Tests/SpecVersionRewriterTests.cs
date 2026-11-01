using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class SpecVersionRewriterTests
{
    [Fact]
    public void NoAutoUpdateMarker_DetectsOptOutDirectiveAndReason()
    {
        var specWithReason = """
            #!NoAutoUpdate: pinned to LTS branch 24.03
            Name:           example
            Version:        1.0.0
            Release:        %autorelease
            """;

        Assert.True(NoAutoUpdateMarker.HasNoAutoUpdate(specWithReason, out var reason));
        Assert.Equal("pinned to LTS branch 24.03", reason);

        var specWithoutMarker = """
            Name:           example
            Version:        1.0.0
            Release:        %autorelease
            """;

        Assert.False(NoAutoUpdateMarker.HasNoAutoUpdate(specWithoutMarker, out var emptyReason));
        Assert.Empty(emptyReason);

        var specWithBlankReason = """
            #!NoAutoUpdate:   
            Name:           example
            Version:        1.0.0
            """;

        Assert.True(NoAutoUpdateMarker.HasNoAutoUpdate(specWithBlankReason, out var fallbackReason));
        Assert.Equal("No reason specified", fallbackReason);
    }

    [Fact]
    public void PlainSpecVersionRewriter_ReplacesOnlyVersionToken_PreservingAlignmentAndComments()
    {
        var original = "Name:           curl\r\nVersion:        8.21.0    # upstream version\r\nRelease:        %autorelease\r\n";
        var rewriter = new PlainSpecVersionRewriter();
        var context = new SpecContext("curl", "curl", original);

        Assert.True(rewriter.CanHandle(context));

        var result = rewriter.Rewrite(context, "8.22.0");

        Assert.True(result.Success);
        Assert.True(result.HasChanged);
        Assert.Equal("Name:           curl\r\nVersion:        8.22.0    # upstream version\r\nRelease:        %autorelease\r\n", result.UpdatedContent);
    }

    [Fact]
    public void PlainSpecVersionRewriter_EmptyUpdate_ReturnsHasChangedFalse()
    {
        var original = "Name:           curl\nVersion:        8.21.0\nRelease:        %autorelease\n";
        var rewriter = new PlainSpecVersionRewriter();
        var context = new SpecContext("curl", "curl", original);

        var result = rewriter.Rewrite(context, "8.21.0");

        Assert.True(result.Success);
        Assert.False(result.HasChanged);
        Assert.Equal(original, result.UpdatedContent);
    }

    [Fact]
    public void PlainSpecVersionRewriter_RejectsPrereleaseWithHyphen()
    {
        var original = "Name:           curl\nVersion:        8.21.0\nRelease:        %autorelease\n";
        var rewriter = new PlainSpecVersionRewriter();
        var context = new SpecContext("curl", "curl", original);

        var result = rewriter.Rewrite(context, "8.22.0-rc.1");

        Assert.False(result.Success);
        Assert.Contains("hyphen", result.ErrorMessage);
    }

    [Fact]
    public void RustCrateSpecRewriter_DeducesBranch_AndSynchronizesFullVersionAndVersion()
    {
        var original = """
            %global crate_name anstream
            %global full_version 0.6.21

            Name:           rust-anstream-0.6
            Version:        0.6.21
            Release:        %autorelease
            Summary:        A library for writing colored text
            """;

        var rewriter = new RustCrateSpecRewriter();
        var context = new SpecContext("rust-anstream-0.6", "rust-anstream-0.6", original);

        Assert.True(rewriter.CanHandle(context));

        // Compatible update within 0.6 branch
        var result = rewriter.Rewrite(context, "0.6.22");
        Assert.True(result.Success);
        Assert.True(result.HasChanged);
        Assert.Contains("%global full_version 0.6.22", result.UpdatedContent);
        Assert.Contains("Version:        0.6.22", result.UpdatedContent);

        // Incompatible update crossing to major 1.0.0
        var crossMajorResult = rewriter.Rewrite(context, "1.0.0");
        Assert.False(crossMajorResult.Success);
        Assert.Contains("does not satisfy branch constraint '0.6'", crossMajorResult.ErrorMessage);
    }

    [Fact]
    public void RustCrateSpecRewriter_HandlesCrateNamesWithHyphens()
    {
        // rust-aes-gcm-0.11
        var original = """
            %global crate_name aes-gcm
            %global full_version 0.11.0

            Name:           rust-aes-gcm-0.11
            Version:        0.11.0
            Release:        %autorelease
            """;

        var rewriter = new RustCrateSpecRewriter();
        var context = new SpecContext("rust-aes-gcm-0.11", "rust-aes-gcm-0.11", original);

        Assert.True(rewriter.CanHandle(context));

        var validResult = rewriter.Rewrite(context, "0.11.1");
        Assert.True(validResult.Success);
        Assert.Contains("0.11.1", validResult.UpdatedContent);

        var invalidResult = rewriter.Rewrite(context, "0.12.0");
        Assert.False(invalidResult.Success);
    }

    [Fact]
    public void RustCrateSpecRewriter_HandlesCrateNamesWithUnderscores_AndDeducesBranch()
    {
        // Tests rand_core (with underscore in crate name) in directory rust-rand-core-0.10
        var original = """
            %global crate_name rand_core
            %global full_version 0.10.0-rc.6

            Name:           rust-rand-core-0.10
            Version:        0.10.0
            Release:        %autorelease
            """;

        var rewriter = new RustCrateSpecRewriter();
        var context = new SpecContext("rust-rand-core-0.10", "rust-rand-core-0.10", original);

        Assert.True(rewriter.CanHandle(context));

        // Compatible within branch 0.10
        var validResult = rewriter.Rewrite(context, "0.10.1");
        Assert.True(validResult.Success);
        Assert.Contains("0.10.1", validResult.UpdatedContent);

        // Incompatible across major / minor branch (e.g. 0.11.0)
        var invalidResult = rewriter.Rewrite(context, "0.11.0");
        Assert.False(invalidResult.Success);
        Assert.Contains("branch constraint", invalidResult.ErrorMessage);
    }

    [Fact]
    public void RustCrateSpecRewriter_PreservesPlusSuffixInFullVersion()
    {
        var original = """
            %global crate_name libloading
            %global full_version 0.4.87+curl-8.19.0

            Name:           rust-libloading
            Version:        0.4.87
            Release:        %autorelease
            """;

        var rewriter = new RustCrateSpecRewriter();
        var context = new SpecContext("rust-libloading", "rust-libloading", original);

        Assert.True(rewriter.CanHandle(context));

        var result = rewriter.Rewrite(context, "0.4.88");
        Assert.True(result.Success);
        Assert.Contains("%global full_version 0.4.88+curl-8.19.0", result.UpdatedContent);
        Assert.Contains("Version:        0.4.88", result.UpdatedContent);
    }

    [Fact]
    public void MutationGuard_WholeLineReplacement_ViolatesBytePreservationInvariant()
    {
        // Guard asserting why whole-line replacement is defective compared to pure token replacement
        var original = "Version:        8.21.0    # upstream comment\n";
        var rewriter = new PlainSpecVersionRewriter();
        var context = new SpecContext("curl", "curl", original);

        var result = rewriter.Rewrite(context, "8.22.0");

        // The true token rewriter MUST preserve the tab/space alignment and trailing comment:
        Assert.Equal("Version:        8.22.0    # upstream comment\n", result.UpdatedContent);

        // A defective / mutated rewriter that replaced the whole line would produce:
        var mutatedWholeLine = "Version: 8.22.0\n";
        Assert.NotEqual(mutatedWholeLine, result.UpdatedContent);
    }

    [Fact]
    public void MutationGuard_DisabledPrereleaseFilter_GeneratesInvalidRpmNevra()
    {
        // Guard asserting why pre-releases must be blocked at rewriter stage
        var context = new SpecContext("curl", "curl", "Version: 8.21.0\n");
        var rewriter = new PlainSpecVersionRewriter();

        var prereleaseResult = rewriter.Rewrite(context, "8.22.0-rc.1");

        // Guard must strictly block versions with hyphens
        Assert.False(prereleaseResult.Success);
        Assert.Contains("pre-release", prereleaseResult.ErrorMessage);
    }
}
