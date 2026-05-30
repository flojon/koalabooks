namespace KoalaBooks.Application.Services;

public interface IAttachmentProvider
{
    string GetDownloadUrl(int id);
}
