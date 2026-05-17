namespace KoalaBooks.Application.Services;

public class WebAttachmentProvider : IAttachmentProvider
{
    private readonly AttachmentService _svc;

    public WebAttachmentProvider(AttachmentService svc)
    {
        _svc = svc;
    }

    public string? GetDownloadUrl(int id) => $"/attachments/{id}";

    public async Task<AttachmentData?> GetAsync(int id)
    {
        var a = await _svc.GetAsync(id);
        return a is null ? null : new AttachmentData(a.Data, a.ContentType, a.FileName);
    }
}
