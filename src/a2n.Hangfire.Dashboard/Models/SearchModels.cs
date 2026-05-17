namespace a2n.Hangfire.Dashboard.Models;

public class SearchRequest
{
    public string Query { get; set; } = "";
    public SearchMode Mode { get; set; } = SearchMode.Auto;
    public int From { get; set; } = 0;
    public int PageSize { get; set; } = 20;

    // Filters (all optional, AND logic between them)
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public List<string> States { get; set; } = new();
    public string Server { get; set; }
    public int? MinDurationSeconds { get; set; }
    public int? MaxDurationSeconds { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Queue { get; set; }
    public string RecurringJobId { get; set; }

    // Content search (searches inside job data — stack trace, console output)
    public string ContentQuery { get; set; }
    public bool SearchStackTrace { get; set; } = false;
    public bool SearchConsoleOutput { get; set; } = false;
}

public enum SearchMode
{
    Auto,
    Id,
    Name,
    Queue,
    Tag,
    Exception
}

public class SearchResult
{
    public List<SearchResultItem> Items { get; set; } = new();
    public long TotalCount { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool TimedOut { get; set; }
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; }
}

public class SearchResultItem
{
    public string JobId { get; set; }
    public string JobName { get; set; }
    public string State { get; set; }
    public string Queue { get; set; }
    public DateTime? LastStateChange { get; set; }
    public DateTime? CreatedAt { get; set; }
    public double? DurationMs { get; set; }
    public string[] Tags { get; set; }
    public string ExceptionExcerpt { get; set; }
    public string ContentExcerpt { get; set; }
    public SearchMatchSource MatchSource { get; set; }
}

public enum SearchMatchSource
{
    Id,
    Name,
    Queue,
    Tag,
    Exception,
    Content
}

public class FilterOptions
{
    public List<string> Queues { get; set; } = new();
    public List<string> Servers { get; set; } = new();
    public List<string> RecurringJobIds { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public bool TagsFeatureAvailable { get; set; }
}

public class FilterPreset
{
    public string Name { get; set; }
    public string Query { get; set; }
    public List<string> States { get; set; } = new();
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string Server { get; set; }
    public int? MinDurationSeconds { get; set; }
    public int? MaxDurationSeconds { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Queue { get; set; }
    public string RecurringJobId { get; set; }
    public string ContentQuery { get; set; }
    public bool SearchStackTrace { get; set; } = false;
    public bool SearchConsoleOutput { get; set; } = false;
}
