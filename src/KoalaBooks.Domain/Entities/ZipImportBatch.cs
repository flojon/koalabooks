namespace KoalaBooks.Domain.Entities;

public class ZipImportBatch
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public uint? StagingOid { get; set; }
    public int TotalEntries { get; set; }
    public int ProcessedEntries { get; set; }
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
    public string SkippedReasonsJson { get; set; } = "[]";
    public bool Done { get; set; }
    public bool Acknowledged { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
