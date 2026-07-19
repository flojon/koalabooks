using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class NoOpZipImportQueue : IZipImportQueue
{
    public void Enqueue(int runId, string fileName, uint stagingOid) { }
}
