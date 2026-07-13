namespace KoalaBooks.Domain.Interfaces;

public interface IDocumentStorage
{
    Task<string> SaveAsync(int documentId, string contentType, Stream data);
    Task<byte[]> LoadAsync(string storageKey);
    Task DeleteAsync(string storageKey);
}
