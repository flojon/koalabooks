namespace KoalaBooks.Domain.Interfaces;

public interface IZipImportQueue
{
    void Enqueue(int batchId);
}
