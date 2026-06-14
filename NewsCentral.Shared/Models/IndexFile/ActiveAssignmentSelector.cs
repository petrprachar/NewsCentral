namespace NewsCentral.Models.IndexFile;

/// <summary>
/// Pure selection helpers over <see cref="PublishedAssignmentIndex"/>: determine whether an
/// assignment is active for a given instant, and pick the newest active assignment matching a
/// predicate. Shared so both NewsService (lock screen) and NewsViewer (poster + wallpaper) apply
/// identical active-window/day-of-week semantics and "newest by PresentationLastModified" tie-break.
/// </summary>
public static class ActiveAssignmentSelector
{
    /// <summary>
    /// An assignment is active when <paramref name="now"/> falls within its schedule window and
    /// today's day number (1=Mon … 7=Sun) is listed in <see cref="PublishedAssignmentIndex.DaysOfWeek"/>.
    /// Schedules use client local time — no timezone conversion.
    /// </summary>
    public static bool IsActive(PublishedAssignmentIndex assignment, DateTime now)
    {
        if (assignment.ScheduleStart > now || assignment.ScheduleEnd < now) return false;

        // DayOfWeek: Sunday=0, Monday=1 … Saturday=6 → map to 1=Mon … 7=Sun
        var todayKey = now.DayOfWeek == DayOfWeek.Sunday ? "7" : ((int)now.DayOfWeek).ToString();
        return assignment.DaysOfWeek.Split(',').Contains(todayKey);
    }

    /// <summary>
    /// Returns the active assignment with the most recent <see cref="PublishedAssignmentIndex.PresentationLastModified"/>
    /// that also satisfies <paramref name="predicate"/>, or <c>null</c> when none qualify.
    /// </summary>
    public static PublishedAssignmentIndex? PickNewestActive(
        IEnumerable<PublishedAssignmentIndex> assignments,
        DateTime now,
        Func<PublishedAssignmentIndex, bool> predicate)
    {
        PublishedAssignmentIndex? best = null;

        foreach (var a in assignments)
        {
            if (!IsActive(a, now)) continue;
            if (!predicate(a)) continue;

            if (best is null || a.PresentationLastModified > best.PresentationLastModified)
                best = a;
        }

        return best;
    }
}
