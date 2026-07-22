namespace KoalaBooks.Domain.Interfaces;

public interface ISieImportQueue
{
    void Enqueue(int runId, string fileName, uint stagingOid, bool overwrite, int? rarId);
}
