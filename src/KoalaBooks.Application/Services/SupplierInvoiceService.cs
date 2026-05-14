using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class SupplierInvoiceService
{
    private readonly AppDbContext _db;

    public SupplierInvoiceService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<SupplierInvoice>> GetAllAsync(int fiscalYearId)
    {
        return await _db.SupplierInvoices
            .Include(s => s.JournalEntry)
            .Include(s => s.PaymentJournalEntry)
            .Where(s => s.FiscalYearId == fiscalYearId)
            .OrderByDescending(s => s.InvoiceDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync();
    }

    public async Task<(SupplierInvoice? Invoice, string? Error)> CreateAsync(SupplierInvoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.SupplierName))
            return (null, "Leverantörsnamn är obligatoriskt.");
        if (invoice.TotalAmount <= 0)
            return (null, "Totalt belopp måste vara större än noll.");
        if (invoice.DueDate < invoice.InvoiceDate)
            return (null, "Förfallodatum kan inte vara före fakturadatum.");

        var fiscalYear = await _db.FiscalYears.FindAsync(invoice.FiscalYearId);
        if (fiscalYear is null) return (null, "Räkenskapsår hittades inte.");
        if (fiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        invoice.CreatedAt = DateTime.UtcNow;
        _db.SupplierInvoices.Add(invoice);
        await _db.SaveChangesAsync();
        return (invoice, null);
    }

    public async Task<(SupplierInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId, int expenseAccountId, int payableAccountId, int? vatAccountId)
    {
        var invoice = await _db.SupplierInvoices
            .Include(s => s.FiscalYear)
            .FirstOrDefaultAsync(s => s.Id == invoiceId);

        if (invoice is null) return (null, "Fakturan hittades inte.");
        if (invoice.JournalEntryId.HasValue) return (null, "Fakturan är redan bokförd.");
        if (invoice.FiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        var lines = new List<JournalEntryLine>
        {
            new() { AccountId = expenseAccountId, DebitAmount = invoice.AmountExclVat, CreditAmount = 0 }
        };

        if (invoice.VatAmount != 0 && vatAccountId.HasValue)
            lines.Add(new() { AccountId = vatAccountId.Value, DebitAmount = invoice.VatAmount, CreditAmount = 0 });

        lines.Add(new() { AccountId = payableAccountId, DebitAmount = 0, CreditAmount = invoice.TotalAmount });

        var entryNumber = await NextEntryNumberAsync(invoice.FiscalYearId);
        var journalEntry = new JournalEntry
        {
            EntryNumber = entryNumber,
            Date = invoice.InvoiceDate,
            Description = $"Leverantörsfaktura {invoice.SupplierName}" + (invoice.InvoiceNumber is not null ? $" #{invoice.InvoiceNumber}" : ""),
            FiscalYearId = invoice.FiscalYearId,
            IsPosted = true,
            CreatedAt = DateTime.UtcNow,
            Lines = lines
        };

        _db.JournalEntries.Add(journalEntry);
        await _db.SaveChangesAsync();

        invoice.JournalEntryId = journalEntry.Id;
        await _db.SaveChangesAsync();

        return (invoice, null);
    }

    public async Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate)
    {
        var minDate = invoiceDate.AddDays(-7);
        var maxDate = dueDate.AddDays(30);

        return await _db.BankTransactions
            .Include(b => b.Account)
            .Where(b => b.Account.FiscalYearId == fiscalYearId)
            .Where(b => b.Status == BankTransactionStatus.Unmatched)
            .Where(b => b.Date >= minDate && b.Date <= maxDate)
            .Where(b => (b.Amount <= -(invoiceTotal - 0.01m) && b.Amount >= -(invoiceTotal + 0.01m)) ||
                        (b.Amount >= invoiceTotal - 0.01m && b.Amount <= invoiceTotal + 0.01m))
            .OrderBy(b => b.Date)
            .ToListAsync();
    }

    public async Task<(SupplierInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId,
        DateOnly paidDate,
        int bankAccountId,
        int payableAccountId,
        int? linkBankTransactionId = null)
    {
        var invoice = await _db.SupplierInvoices
            .Include(s => s.FiscalYear)
            .FirstOrDefaultAsync(s => s.Id == invoiceId);

        if (invoice is null) return (null, "Fakturan hittades inte.");
        if (invoice.IsPaid) return (null, "Fakturan är redan betald.");
        if (invoice.FiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        var entryNumber = await NextEntryNumberAsync(invoice.FiscalYearId);
        var paymentEntry = new JournalEntry
        {
            EntryNumber = entryNumber,
            Date = paidDate,
            Description = $"Betalning {invoice.SupplierName}" + (invoice.InvoiceNumber is not null ? $" #{invoice.InvoiceNumber}" : ""),
            FiscalYearId = invoice.FiscalYearId,
            IsPosted = true,
            CreatedAt = DateTime.UtcNow,
            Lines =
            [
                new() { AccountId = payableAccountId, DebitAmount = invoice.TotalAmount, CreditAmount = 0 },
                new() { AccountId = bankAccountId,    DebitAmount = 0, CreditAmount = invoice.TotalAmount }
            ]
        };

        _db.JournalEntries.Add(paymentEntry);
        invoice.IsPaid = true;
        invoice.PaidDate = paidDate;
        await _db.SaveChangesAsync();

        invoice.PaymentJournalEntryId = paymentEntry.Id;

        if (linkBankTransactionId.HasValue)
        {
            var bankTx = await _db.BankTransactions.FindAsync(linkBankTransactionId.Value);
            if (bankTx is not null)
            {
                bankTx.JournalEntryId = paymentEntry.Id;
                bankTx.Status = BankTransactionStatus.Matched;
            }
        }

        await _db.SaveChangesAsync();

        return (invoice, null);
    }

    public async Task<string?> DeleteAsync(int invoiceId)
    {
        var invoice = await _db.SupplierInvoices.FindAsync(invoiceId);
        if (invoice is null) return "Fakturan hittades inte.";
        if (invoice.JournalEntryId.HasValue) return "Bokförda fakturor kan inte raderas.";
        if (invoice.IsPaid) return "Betalda fakturor kan inte raderas.";

        _db.SupplierInvoices.Remove(invoice);
        await _db.SaveChangesAsync();
        return null;
    }

    public async Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix)
    {
        return await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId && a.AccountNumber.StartsWith(prefix))
            .OrderBy(a => a.AccountNumber)
            .FirstOrDefaultAsync();
    }

    public async Task<List<string>> GetSuppliersAsync(int fiscalYearId)
    {
        return await _db.SupplierInvoices
            .Where(s => s.FiscalYearId == fiscalYearId)
            .Select(s => s.SupplierName)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync();
    }

    public async Task<HashSet<int>> GetLinkedJournalEntryIdsAsync(int fiscalYearId)
    {
        var ids = await _db.SupplierInvoices
            .Where(s => s.FiscalYearId == fiscalYearId && s.JournalEntryId.HasValue)
            .Select(s => s.JournalEntryId!.Value)
            .ToListAsync();
        return [..ids];
    }

    public async Task<List<JournalEntry>> GetLinkableEntriesAsync(int fiscalYearId)
    {
        var linkedIds = await GetLinkedJournalEntryIdsAsync(fiscalYearId);
        var entries = await _db.JournalEntries
            .Include(j => j.Lines)
            .Where(j => j.FiscalYearId == fiscalYearId && j.IsPosted && !j.IsClosingEntry)
            .OrderByDescending(j => j.Date)
            .ToListAsync();
        return entries.Where(j => !linkedIds.Contains(j.Id)).ToList();
    }

    public async Task<(SupplierInvoice? Invoice, string? Error)> CreateFromEntryAsync(
        int journalEntryId,
        SupplierInvoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.SupplierName))
            return (null, "Leverantörsnamn är obligatoriskt.");
        if (invoice.TotalAmount <= 0)
            return (null, "Totalt belopp måste vara större än noll.");
        if (invoice.DueDate < invoice.InvoiceDate)
            return (null, "Förfallodatum kan inte vara före fakturadatum.");

        var fiscalYear = await _db.FiscalYears.FindAsync(invoice.FiscalYearId);
        if (fiscalYear is null) return (null, "Räkenskapsår hittades inte.");

        var entry = await _db.JournalEntries.FindAsync(journalEntryId);
        if (entry is null) return (null, "Verifikationen hittades inte.");
        if (entry.FiscalYearId != invoice.FiscalYearId)
            return (null, "Verifikationen tillhör ett annat räkenskapsår.");

        var alreadyLinked = await _db.SupplierInvoices.AnyAsync(s => s.JournalEntryId == journalEntryId);
        if (alreadyLinked) return (null, "Verifikationen är redan kopplad till en faktura.");

        invoice.JournalEntryId = journalEntryId;
        invoice.CreatedAt = DateTime.UtcNow;
        _db.SupplierInvoices.Add(invoice);
        await _db.SaveChangesAsync();

        return (invoice, null);
    }

    private async Task<int> NextEntryNumberAsync(int fiscalYearId)
    {
        return (await _db.JournalEntries
            .Where(j => j.FiscalYearId == fiscalYearId)
            .MaxAsync(j => (int?)j.EntryNumber) ?? 0) + 1;
    }
}
