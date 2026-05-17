using KoalaBooks.Application.Services;

namespace KoalaBooks.Tests;

public class AttachmentProviderTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void WebAttachmentProvider_GetDownloadUrl_ReturnsExpectedPath()
    {
        var provider = new WebAttachmentProvider(new AttachmentService(_fx.Db));
        Assert.Equal("/attachments/42", provider.GetDownloadUrl(42));
    }

    [Fact]
    public async Task WebAttachmentProvider_GetAsync_ReturnsNullForMissingId()
    {
        var provider = new WebAttachmentProvider(new AttachmentService(_fx.Db));
        var result = await provider.GetAsync(99999);
        Assert.Null(result);
    }
}
