using System.Collections;

namespace abaci_bot.Contexts;

/// <summary>
/// An insertion-ordered set collection.
/// Combines the uniqueness and fast lookup of a hash set with the predictable iteration order of a list.
/// Pulls complexity downward by ensuring mutex arbitration and diff algorithms deterministically observe
/// the exact declaration sequence of governance modules, even after intermediate removals.
/// </summary>
/// <typeparam name="T">The type of elements in the set.</typeparam>
public class OrderedSet<T> : ISet<T>, IReadOnlyCollection<T>
{
    private readonly List<T> _list = new();
    private readonly HashSet<T> _set;
    private readonly IEqualityComparer<T> _comparer;

    public OrderedSet(IEqualityComparer<T>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _set = new HashSet<T>(_comparer);
    }

    public OrderedSet(IEnumerable<T> collection, IEqualityComparer<T>? comparer = null)
        : this(comparer)
    {
        if (collection != null)
        {
            foreach (var item in collection)
            {
                Add(item);
            }
        }
    }

    public int Count => _list.Count;

    public bool IsReadOnly => false;

    public bool Add(T item)
    {
        if (_set.Add(item))
        {
            _list.Add(item);
            return true;
        }
        return false;
    }

    void ICollection<T>.Add(T item) => Add(item);

    public bool Remove(T item)
    {
        if (_set.Remove(item))
        {
            _list.RemoveAll(x => _comparer.Equals(x, item));
            return true;
        }
        return false;
    }

    public void Clear()
    {
        _set.Clear();
        _list.Clear();
    }

    public bool Contains(T item) => _set.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _list.CopyTo(array, arrayIndex);

    public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void UnionWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
            return;

        foreach (var item in other)
        {
            Add(item);
        }
    }

    public void IntersectWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
            return;

        var otherSet = new HashSet<T>(other, _comparer);
        _set.IntersectWith(otherSet);
        _list.RemoveAll(item => !otherSet.Contains(item));
    }

    public void ExceptWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
        {
            Clear();
            return;
        }

        var otherSet = new HashSet<T>(other, _comparer);
        foreach (var item in otherSet)
        {
            Remove(item);
        }
    }

    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
        {
            Clear();
            return;
        }

        var otherSet = new HashSet<T>(other, _comparer);
        foreach (var item in otherSet)
        {
            if (!Remove(item))
            {
                Add(item);
            }
        }
    }

    public bool IsSubsetOf(IEnumerable<T> other) => _set.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<T> other) => _set.IsSupersetOf(other);

    public bool IsProperSubsetOf(IEnumerable<T> other) => _set.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<T> other) => _set.IsProperSupersetOf(other);

    public bool Overlaps(IEnumerable<T> other) => _set.Overlaps(other);

    public bool SetEquals(IEnumerable<T> other) => _set.SetEquals(other);
}
