using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class SupplierInvoiceServiceOrganisationScopeTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly SupplierInvoiceService _svc;

    public SupplierInvoiceServiceOrganisationScopeTests()
    {
        _f = new TestFixture();
        _svc = new SupplierInvoiceService(_f.Db, TestFixture.MakeTenant(_f.OrganisationId));
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task GetAllForOrganisationAsync_SpansMultipleOpenFiscalYears()
    {
        var fy2025 = _f.CreateFiscalYear("2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        _f.Db.SupplierInvoices.AddRange(
            new SupplierInvoice { FiscalYearId = fy2025.Id, SupplierName = "A", InvoiceDate = new DateOnly(2025, 6, 1), DueDate = new DateOnly(2025, 7, 1), TotalAmount = 100, IsPaid = false },
            new SupplierInvoice { FiscalYearId = fy2026.Id, SupplierName = "B", InvoiceDate = new DateOnly(2026, 6, 1), DueDate = new DateOnly(2026, 7, 1), TotalAmount = 200, IsPaid = false },
            new SupplierInvoice { FiscalYearId = fy2026.Id, SupplierName = "C", InvoiceDate = new DateOnly(2026, 6, 1), DueDate = new DateOnly(2026, 7, 1), TotalAmount = 300, IsPaid = true });
        await _f.Db.SaveChangesAsync();

        var all = await _svc.GetAllForOrganisationAsync();
        var unpaidCount = await _svc.CountUnpaidForOrganisationAsync();

        Assert.Equal(3, all.Count);
        Assert.Equal(2, unpaidCount);
    }
}
