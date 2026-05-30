using KoalaBooks.Application.Services;

namespace KoalaBooks.Web.Services;

public class WebAttachmentProvider : IAttachmentProvider
{
    public string GetDownloadUrl(int id) => $"/attachments/{id}";
}
