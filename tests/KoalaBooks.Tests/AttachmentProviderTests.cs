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

    [Fact]
    public async Task WebAttachmentProvider_GetAsync_ReturnsAttachmentData_WhenExists()
    {
        var fy = _fx.CreateFiscalYear();
        var (debit, credit, _, _, _) = _fx.CreateStandardAccounts(fy.Id);
        var entry = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 100m);

        var svc = new AttachmentService(_fx.Db);
        var added = await svc.AddAsync(entry.Id, "test.pdf", "application/pdf", new byte[] { 1, 2, 3 });
        Assert.NotNull(added);

        var provider = new WebAttachmentProvider(svc);
        var result = await provider.GetAsync(added.Id);

        Assert.NotNull(result);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal("test.pdf", result.FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Data);
    }
}
