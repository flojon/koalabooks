using KoalaBooks.Application.Services;

namespace KoalaBooks.Web.Services;

public class WebAttachmentProvider : IAttachmentProvider
{
    private readonly AttachmentService _attachmentService;

    public WebAttachmentProvider(AttachmentService svc)
    {
        _attachmentService = svc;
    }

    public string? GetDownloadUrl(int id) => $"/attachments/{id}";

    public async Task<AttachmentData?> GetAsync(int id)
    {
        var a = await _attachmentService.GetAsync(id);
        return a is null ? null : new AttachmentData(a.Data, a.ContentType, a.FileName);
    }
}
