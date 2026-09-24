namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// Shared ordering rule for sortable grids: rows without a value for the sort key always go last,
/// whichever the direction. Callers chain a tie-breaker with <c>ThenBy</c> so the order stays stable
/// across refreshes.
/// </summary>
internal static class GridSort
{
    public static IOrderedEnumerable<T> By<T, TKey>(
        IEnumerable<T> items,
        Func<T, TKey> key,
        Func<T, bool> hasValue,
        IComparer<TKey> comparer,
        bool descending)
    {
        var missingLast = items.OrderBy(item => hasValue(item) ? 0 : 1);
        return descending
            ? missingLast.ThenByDescending(key, comparer)
            : missingLast.ThenBy(key, comparer);
    }

    /// <summary>
    /// Sorts <paramref name="items"/> by the key <paramref name="keyFor"/> returns for the active
    /// column. A <c>null</c> selector (e.g. for the <c>None</c> column) keeps the original order; a
    /// <c>null</c> key value counts as missing. Strings compare case-insensitively.
    /// </summary>
    public static IEnumerable<T> Apply<T, TColumn>(
        IEnumerable<T> items,
        GridSortState<TColumn> state,
        Func<TColumn, Func<T, object>> keyFor,
        Func<T, string> tieBreak = null)
        where TColumn : struct, Enum
    {
        if (items is null)
            return Enumerable.Empty<T>();

        var key = keyFor(state.Column);
        if (key is null)
            return items;

        var ordered = By(items, key, item => key(item) is not null, ValueComparer.Instance, state.Descending);
        return tieBreak is null ? ordered : ordered.ThenBy(tieBreak, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ValueComparer : IComparer<object>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(object x, object y) =>
            x is string a && y is string b
                ? StringComparer.OrdinalIgnoreCase.Compare(a, b)
                : Comparer<object>.Default.Compare(x, y);
    }
}

/// <summary>
/// Sort state for a grid. Selecting a new column sorts it ascending; selecting the active column
/// again flips the direction.
/// </summary>
internal sealed class GridSortState<TColumn> where TColumn : struct, Enum
{
    public GridSortState(TColumn initial = default, bool descending = false)
    {
        Column = initial;
        Descending = descending;
    }

    public TColumn Column { get; private set; }

    public bool Descending { get; private set; }

    public bool IsActive(TColumn column) => EqualityComparer<TColumn>.Default.Equals(Column, column);

    public void Toggle(TColumn column)
    {
        if (IsActive(column))
        {
            Descending = !Descending;
        }
        else
        {
            Column = column;
            Descending = false;
        }
    }
}
