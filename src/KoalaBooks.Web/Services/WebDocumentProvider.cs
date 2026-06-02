using KoalaBooks.Application.Services;

namespace KoalaBooks.Web.Services;

public class WebDocumentProvider : IDocumentProvider
{
    public string GetDownloadUrl(int documentId) => $"/documents/{documentId}";
}
