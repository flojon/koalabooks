namespace KoalaBooks.Application.Services;

public interface IAttachmentProvider
{
    string? GetDownloadUrl(int id);
    Task<AttachmentData?> GetAsync(int id);
}
