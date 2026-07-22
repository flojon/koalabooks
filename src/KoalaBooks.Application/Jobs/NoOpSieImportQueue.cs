using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class NoOpSieImportQueue : ISieImportQueue
{
    public void Enqueue(int runId, string fileName, uint stagingOid, bool overwrite, int? rarId) { }
}
