using System.Reflection;
using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class LabelDocumentationTests
{
    [Fact]
    public void EveryManagedLabel_IsDocumentedInLabelsReference()
    {
        var docsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "labels.md"));
        Assert.True(File.Exists(docsPath), $"docs/labels.md must exist at {docsPath}");

        var docs = File.ReadAllText(docsPath);
        var labels = typeof(GitHubLabels)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(labels);

        // The label constants are the source of truth. The documentation must list
        // every value so a rename cannot leave the reference stale.
        var missing = labels
            .Where(label => !docs.Contains(label, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"docs/labels.md is missing managed label(s): {string.Join(", ", missing)}");
    }
}
