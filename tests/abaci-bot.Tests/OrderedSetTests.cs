using abaci_bot.Contexts;
using Xunit;

namespace abaci_bot.Tests;

public class OrderedSetTests
{
    [Fact]
    public void Add_PreservesInsertionOrder()
    {
        var set = new OrderedSet<string> { "first", "second", "third" };

        Assert.Equal(3, set.Count);
        Assert.Equal(new[] { "first", "second", "third" }, set);
    }

    [Fact]
    public void Add_DuplicateItem_ReturnsFalse_AndPreservesOriginalOrder()
    {
        var set = new OrderedSet<string> { "a", "b", "c" };

        var added = set.Add("b");

        Assert.False(added);
        Assert.Equal(3, set.Count);
        Assert.Equal(new[] { "a", "b", "c" }, set);
    }

    [Fact]
    public void Remove_PreservesRelativeOrderOfRemainingItems()
    {
        var set = new OrderedSet<string> { "x", "y", "z" };

        var removed = set.Remove("y");

        Assert.True(removed);
        Assert.Equal(2, set.Count);
        Assert.Equal(new[] { "x", "z" }, set);
        Assert.DoesNotContain("y", set);
    }

    [Fact]
    public void Remove_ThenReAdd_AppendsToEnd()
    {
        var set = new OrderedSet<string> { "alpha", "beta", "gamma" };

        set.Remove("alpha");
        set.Add("alpha");

        Assert.Equal(new[] { "beta", "gamma", "alpha" }, set);
    }

    [Fact]
    public void CaseInsensitiveComparer_TreatsVariantsAsEqual_PreservesFirstCasing()
    {
        var set = new OrderedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Workflow: In Dev",
            "WORKFLOW: IN DEV"
        };

        Assert.Single(set);
        Assert.Equal("Workflow: In Dev", set.First());
        Assert.Contains("workflow: in dev", set);

        var removed = set.Remove("workflow: IN DEV");
        Assert.True(removed);
        Assert.Empty(set);
    }

    [Fact]
    public void UnionWith_AppendsOnlyNewItemsInOrder()
    {
        var set = new OrderedSet<int> { 1, 3, 5 };
        set.UnionWith(new[] { 3, 2, 4, 1, 6 });

        Assert.Equal(new[] { 1, 3, 5, 2, 4, 6 }, set);
    }

    [Fact]
    public void IntersectWith_RetainsExistingItemsInOriginalOrder()
    {
        var set = new OrderedSet<string> { "apple", "banana", "cherry", "date" };
        set.IntersectWith(new[] { "date", "banana", "fig" });

        Assert.Equal(new[] { "banana", "date" }, set);
    }

    [Fact]
    public void ExceptWith_RemovesMatchingItems()
    {
        var set = new OrderedSet<int> { 10, 20, 30, 40 };
        set.ExceptWith(new[] { 20, 40, 50 });

        Assert.Equal(new[] { 10, 30 }, set);
    }

    [Fact]
    public void SymmetricExceptWith_TogglesMembership()
    {
        var set = new OrderedSet<int> { 1, 2, 3 };
        set.SymmetricExceptWith(new[] { 2, 3, 4 });

        Assert.Equal(new[] { 1, 4 }, set);
    }

    [Fact]
    public void SetRelationships_EvaluateCorrectly()
    {
        var set = new OrderedSet<int> { 1, 2, 3 };

        Assert.True(set.IsSubsetOf(new[] { 1, 2, 3, 4 }));
        Assert.True(set.IsProperSubsetOf(new[] { 1, 2, 3, 4 }));
        Assert.False(set.IsProperSubsetOf(new[] { 1, 2, 3 }));

        Assert.True(set.IsSupersetOf(new[] { 1, 2 }));
        Assert.True(set.IsProperSupersetOf(new[] { 1, 2 }));
        Assert.False(set.IsProperSupersetOf(new[] { 1, 2, 3 }));

        Assert.True(set.Overlaps(new[] { 3, 4, 5 }));
        Assert.False(set.Overlaps(new[] { 4, 5, 6 }));

        Assert.True(set.SetEquals(new[] { 3, 2, 1 }));
    }

    [Fact]
    public void Clear_And_CopyTo_WorkAsExpected()
    {
        var set = new OrderedSet<string> { "item1", "item2" };
        var arr = new string[3];
        set.CopyTo(arr, 1);

        Assert.Null(arr[0]);
        Assert.Equal("item1", arr[1]);
        Assert.Equal("item2", arr[2]);

        set.Clear();
        Assert.Empty(set);
    }

    [Fact]
    public void Constructor_WithCollection_InitializesItemsInOrder()
    {
        var source = new[] { "one", "two", "three" };
        var set = new OrderedSet<string>(source);

        Assert.Equal(source, set);
        Assert.False(set.IsReadOnly);

        ICollection<string> col = set;
        col.Add("four");
        Assert.Contains("four", set);
    }
}
