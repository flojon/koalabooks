using KoalaBooks.Application.Services;

namespace KoalaBooks.Tests;

public class DocumentMetaTests
{
    [Fact]
    public void ResolvePrefillDate_PrefersPersistedDocumentDateOverExtractedInvoiceDate()
    {
        var persisted = new DateOnly(2026, 3, 1);
        var extracted = new DateOnly(2026, 1, 15);

        var result = DocumentMeta.ResolvePrefillDate(persisted, extracted);

        Assert.Equal(persisted.ToDateTime(TimeOnly.MinValue), result);
    }

    [Fact]
    public void ResolvePrefillDate_FallsBackToExtractedInvoiceDate_WhenNoPersistedDate()
    {
        var extracted = new DateOnly(2026, 1, 15);

        var result = DocumentMeta.ResolvePrefillDate(null, extracted);

        Assert.Equal(extracted.ToDateTime(TimeOnly.MinValue), result);
    }

    [Fact]
    public void ResolvePrefillDate_ReturnsNull_WhenNeitherDateAvailable()
    {
        var result = DocumentMeta.ResolvePrefillDate(null, null);

        Assert.Null(result);
    }
}
