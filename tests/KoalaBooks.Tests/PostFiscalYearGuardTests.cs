using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

/// <summary>
/// Regression tests for PostAsync fiscal year guard.
/// Bug: PostAsync does not check whether the fiscal year is closed. A user
/// can post draft entries even after the year has been closed, breaking the
/// integrity of the closed period.
/// </summary>
public class PostFiscalYearGuardTests : IDisposable
{
    private readonly TestFixture _f;

    public PostFiscalYearGuardTests()
    {
        _f = new TestFixture();
    }

    public void Dispose() => _f.Dispose();

    /// <summary>
    /// PostAsync should fail with an error when the fiscal year is closed.
    /// Even if a draft exists from before the year was closed, it must not
    /// be postable after closing.
    /// </summary>
    [Fact]
    public async Task Post_FailsInClosedFiscalYear()
    {
        // Arrange: create a draft entry while year is open
        var fy = _f.CreateFiscalYear();
        var cash = _f.CreateAccount(fy.Id, "1910", "Kassa", AccountClass.Asset);
        var revenue = _f.CreateAccount(fy.Id, "3010", "Försäljning", AccountClass.Revenue);

        var entry = _f.MakeEntry(fy.Id, cash.Id, revenue.Id, 5_000m);
        var (created, createErr) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(createErr);
        Assert.NotNull(created);
        Assert.False(created.IsPosted);

        // Close the fiscal year
        fy.IsClosed = true;
        fy.ClosedAt = DateTime.UtcNow;
        await _f.Db.SaveChangesAsync();

        // Act: try to post in the closed year
        var postError = await _f.JournalEntryService.PostAsync(created.Id);

        // Assert: should fail with a meaningful error
        Assert.NotNull(postError);
        Assert.Contains("closed", postError, StringComparison.OrdinalIgnoreCase);
    }
}
