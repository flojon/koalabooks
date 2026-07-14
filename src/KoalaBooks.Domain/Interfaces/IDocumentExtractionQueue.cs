namespace KoalaBooks.Domain.Interfaces;

public interface IDocumentExtractionQueue
{
    void Enqueue(int documentId);
}
