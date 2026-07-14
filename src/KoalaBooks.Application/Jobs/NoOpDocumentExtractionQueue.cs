using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class NoOpDocumentExtractionQueue : IDocumentExtractionQueue
{
    public void Enqueue(int documentId) { }
}
