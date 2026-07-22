using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

public class SupplierInvoiceReadWriteTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;

    public SupplierInvoiceReadWriteTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
    }

    public void Dispose() => _f.Dispose();

    private SupplierInvoice MakeInvoice(string supplier = "Acme AB", decimal total = 1000m) => new()
    {
        FiscalYearId = _fy.Id,
        SupplierName = supplier,
        InvoiceDate = new DateOnly(2026, 3, 1),
        DueDate = new DateOnly(2026, 3, 31),
        AmountExclVat = 800m,
        VatAmount = 200m,
        TotalAmount = total
    };

    [Fact]
    public async Task GetByIdAsync_ExistingInvoice_ReturnsIt()
    {
        var (created, error) = await _f.SupplierInvoiceService.CreateAsync(MakeInvoice());
        Assert.Null(error);
        Assert.NotNull(created);

        var found = await _f.SupplierInvoiceService.GetByIdAsync(created.Id);

        Assert.NotNull(found);
        Assert.Equal("Acme AB", found.SupplierName);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var found = await _f.SupplierInvoiceService.GetByIdAsync(999999);
        Assert.Null(found);
    }

    [Fact]
    public async Task UpdateAsync_DraftInvoice_UpdatesFields()
    {
        var (created, error) = await _f.SupplierInvoiceService.CreateAsync(MakeInvoice());
        Assert.Null(error);
        Assert.NotNull(created);

        var update = new SupplierInvoice
        {
            Id = created.Id,
            SupplierName = "Acme AB (updated)",
            InvoiceDate = new DateOnly(2026, 3, 2),
            DueDate = new DateOnly(2026, 4, 1),
            AmountExclVat = 900m,
            VatAmount = 225m,
            TotalAmount = 1125m
        };

        var (updated, updateError) = await _f.SupplierInvoiceService.UpdateAsync(update);

        Assert.Null(updateError);
        Assert.NotNull(updated);
        Assert.Equal("Acme AB (updated)", updated.SupplierName);
        Assert.Equal(1125m, updated.TotalAmount);
    }

    [Fact]
    public async Task UpdateAsync_PostedInvoice_ReturnsError()
    {
        var (created, _) = await _f.SupplierInvoiceService.CreateAsync(MakeInvoice());
        var (_, liability, _, _, expense) = _f.CreateStandardAccounts(_fy.Id);
        var vat = _f.CreateAccount(_fy.Id, "2641", "Ingående moms", AccountClass.Asset);
        var (posted, postError) = await _f.SupplierInvoiceService.PostAsync(created!.Id, expense.Id, liability.Id, vat.Id);
        Assert.Null(postError);
        Assert.NotNull(posted);

        var update = new SupplierInvoice
        {
            Id = created.Id,
            SupplierName = "Should not apply",
            InvoiceDate = created.InvoiceDate,
            DueDate = created.DueDate,
            AmountExclVat = created.AmountExclVat,
            VatAmount = created.VatAmount,
            TotalAmount = created.TotalAmount
        };

        var (updated, error) = await _f.SupplierInvoiceService.UpdateAsync(update);

        Assert.Null(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsError()
    {
        var update = MakeInvoice();
        update.Id = 999999;

        var (updated, error) = await _f.SupplierInvoiceService.UpdateAsync(update);

        Assert.Null(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task DeleteAsync_ClosedFiscalYear_ReturnsError()
    {
        var (created, error) = await _f.SupplierInvoiceService.CreateAsync(MakeInvoice());
        Assert.Null(error);
        Assert.NotNull(created);

        _fy.IsClosed = true;
        await _f.Db.SaveChangesAsync();

        var deleteError = await _f.SupplierInvoiceService.DeleteAsync(created.Id);

        Assert.NotNull(deleteError);
    }
}
