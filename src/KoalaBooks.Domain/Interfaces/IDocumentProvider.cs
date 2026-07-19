namespace KoalaBooks.Domain.Interfaces;

public interface IDocumentProvider
{
    string GetDownloadUrl(int documentId);
}
