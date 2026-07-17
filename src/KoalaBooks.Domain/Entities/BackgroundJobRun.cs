using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Entities;

public class BackgroundJobRun
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public BackgroundJobType JobType { get; set; }
    public BackgroundJobStatus Status { get; set; } = BackgroundJobStatus.Pending;
    public int ProcessedCount { get; set; }
    public int? TotalCount { get; set; }          // null where progress isn't meaningful (e.g. BAS import)
    public string? ResultJson { get; set; }         // job-specific payload, read only by the page that knows its shape
    public bool Acknowledged { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
