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
