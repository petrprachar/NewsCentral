namespace NewsCentral.Ui;

/// <summary>
/// Tri-state summary of a selection over a currently-SHOWN subset of keys — the master checkbox's
/// three visual states. <see cref="None"/> for an empty shown set (nothing to select).
/// </summary>
public enum SelectionState
{
    None,
    Some,
    All
}

/// <summary>
/// UI-2: generic multi-select state for a list that can be filtered/searched. The selection itself
/// (<see cref="Selected"/>) is always keyed by the full set of selectable keys (e.g. AssignmentID) —
/// a filter only changes what is SHOWN, never what is selected; <see cref="ToggleAll"/> and
/// <see cref="StateOf"/> both take the currently-shown subset explicitly for that reason. Pure, no
/// I/O. Not thread-safe — used from the UI thread only, exactly like the page state it replaces.
/// </summary>
public sealed class SelectionModel<TKey>
{
    private readonly HashSet<TKey> _selected;

    public SelectionModel(IEqualityComparer<TKey>? comparer = null)
    {
        _selected = comparer != null ? new HashSet<TKey>(comparer) : new HashSet<TKey>();
    }

    public IReadOnlyCollection<TKey> Selected => _selected;

    public int Count => _selected.Count;

    public bool IsSelected(TKey key) => _selected.Contains(key);

    public void Set(TKey key, bool selected)
    {
        if (selected)
            _selected.Add(key);
        else
            _selected.Remove(key);
    }

    public void Clear() => _selected.Clear();

    /// <summary>
    /// <see cref="SelectionState.None"/> for an empty <paramref name="shown"/> set or none of it
    /// selected; <see cref="SelectionState.All"/> when every shown key is selected;
    /// <see cref="SelectionState.Some"/> otherwise.
    /// </summary>
    public SelectionState StateOf(IEnumerable<TKey> shown)
    {
        var shownList = AsList(shown);
        if (shownList.Count == 0)
            return SelectionState.None;

        var selectedCount = shownList.Count(_selected.Contains);
        if (selectedCount == 0)
            return SelectionState.None;

        return selectedCount == shownList.Count ? SelectionState.All : SelectionState.Some;
    }

    /// <summary>
    /// From <see cref="SelectionState.None"/> or <see cref="SelectionState.Some"/>: selects every
    /// key in <paramref name="shown"/>. From <see cref="SelectionState.All"/>: deselects every key
    /// in <paramref name="shown"/>. Keys outside <paramref name="shown"/> (hidden by a filter) are
    /// never touched either way.
    /// </summary>
    public void ToggleAll(IEnumerable<TKey> shown)
    {
        var shownList = AsList(shown);
        var state = StateOf(shownList);

        foreach (var key in shownList)
        {
            if (state == SelectionState.All)
                _selected.Remove(key);
            else
                _selected.Add(key);
        }
    }

    /// <summary>
    /// Drops every selected key not present in <paramref name="existing"/> — called after a reload
    /// so a selection never outlives the item it pointed at (e.g. an item a bulk run just acted on
    /// and that disappeared from the reloaded list).
    /// </summary>
    public void RemoveMissing(IEnumerable<TKey> existing)
    {
        var existingSet = existing as HashSet<TKey> ?? new HashSet<TKey>(existing);
        _selected.RemoveWhere(k => !existingSet.Contains(k));
    }

    private static IReadOnlyList<TKey> AsList(IEnumerable<TKey> source) =>
        source as IReadOnlyList<TKey> ?? source.ToList();
}
