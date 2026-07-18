using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class NoOpZipImportQueue : IZipImportQueue
{
    public void Enqueue(int batchId) { }
}
