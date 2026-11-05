namespace Overseer.Intents;

/// <summary>Which Intents the Backlog shows.</summary>
public enum BacklogFilter
{
    /// <summary>Every Intent not Done or Cancelled.</summary>
    Open,

    /// <summary>The Intents that are Done or Cancelled.</summary>
    Closed,
}

/// <summary>The filter as the Backlog's address says it. This is the one place that maps between the two.</summary>
internal static class BacklogFilterAddress
{
    /// <summary>The query parameter that says the filter.</summary>
    public const string QueryParameter = "filter";

    /// <summary>The address of the Backlog under this filter.</summary>
    public static string Address(this BacklogFilter filter) => $"/?{QueryParameter}={ValueOf(filter)}";

    /// <summary>The filter that a value in the address means. Anything else is Open, as a filter the Portal doesn't offer shouldn't hide the Backlog.</summary>
    public static BacklogFilter FromValue(string? value) =>
        Enum.GetValues<BacklogFilter>().Where(filter => string.Equals(ValueOf(filter), value, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(BacklogFilter.Open).Single();

    private static string ValueOf(BacklogFilter filter) => filter.ToString().ToLowerInvariant();
}
