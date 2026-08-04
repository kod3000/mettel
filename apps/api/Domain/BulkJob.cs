namespace Bruin.Api.Domain;

public static class BulkJobStatuses
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completedWithErrors";
    // Reserved for job-level faults only (unreadable file, bad header). A job
    // with any *row* failure ends `completedWithErrors`. See contract.
    public const string Failed = "failed";
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Queued, Processing, Completed, CompletedWithErrors, Failed };
}

public sealed class BulkJob
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Status { get; set; } = BulkJobStatuses.Queued;
    public string FileName { get; set; } = "";
    // Absolute path on the worker-visible volume where the raw upload sits.
    public string FilePath { get; set; } = "";
    public int TotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public int SucceededRows { get; set; }
    public int FailedRows { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BulkJobError
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public Guid ClientId { get; set; }
    public int RowNumber { get; set; }
    public string? ServiceNumber { get; set; }
    public string Reason { get; set; } = "";
    public string RawLine { get; set; } = "";
}
