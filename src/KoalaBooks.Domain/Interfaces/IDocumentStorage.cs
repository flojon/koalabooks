namespace KoalaBooks.Domain.Interfaces;

public interface IDocumentStorage
{
    Task<(string StorageKey, long FileSize)> SaveAsync(int documentId, string contentType, Func<Stream> openData);
    Task<byte[]> LoadAsync(string storageKey);
    Task DeleteAsync(string storageKey);
}
