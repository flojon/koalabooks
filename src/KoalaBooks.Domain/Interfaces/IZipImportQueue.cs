namespace KoalaBooks.Domain.Interfaces;

public interface IZipImportQueue
{
    void Enqueue(int runId, string fileName, uint stagingOid);
}
