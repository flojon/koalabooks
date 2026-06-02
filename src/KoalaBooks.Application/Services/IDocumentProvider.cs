namespace KoalaBooks.Application.Services;

public interface IDocumentProvider
{
    string GetDownloadUrl(int documentId);
}
