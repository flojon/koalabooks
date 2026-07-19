using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class SupplierInvoiceService : ISupplierInvoiceService
{
    public const string NotFoundMessage = "Fakturan hittades inte.";

    private readonly AppDbContext _db;

    public SupplierInvoiceService(AppDbContext db)
    {
        _db = db;
    }

    public Task<int> CountUnpaidAsync(int fiscalYearId) =>
        _db.SupplierInvoices.CountAsync(s => s.FiscalYearId == fiscalYearId && !s.IsPaid);

    public async Task<List<SupplierInvoice>> GetAllAsync(int fiscalYearId)
    {
        return await _db.SupplierInvoices
            .Include(s => s.JournalEntry)
            .Include(s => s.PaymentJournalEntry)
            .Where(s => s.FiscalYearId == fiscalYearId)
            .OrderByDescending(s => s.InvoiceDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<SupplierInvoice?> GetByIdAsync(int id)
    {
        return await _db.SupplierInvoices
            .Include(s => s.JournalEntry)
            .Include(s => s.PaymentJournalEntry)
            .FirstOrDefaultAsync(s => s.Id == id).ConfigureAwait(false);
    }

    public async Task<(SupplierInvoice? Invoice, string? Error)> UpdateAsync(SupplierInvoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.SupplierName))
            return (null, "Leverantörsnamn är obligatoriskt.");
        if (invoice.TotalAmount <= 0)
            return (null, "Totalt belopp måste vara större än noll.");
        if (invoice.DueDate < invoice.InvoiceDate)
            return (null, "Förfallodatum kan inte vara före fakturadatum.");

        var existing = await _db.SupplierInvoices
            .Include(s => s.FiscalYear)
            .FirstOrDefaultAsync(s => s.Id == invoice.Id).ConfigureAwait(false);

        if (existing is null) return (null, NotFoundMessage);
        if (existing.JournalEntryId.HasValue) return (null, "Bokförda fakturor kan inte uppdateras.");
        if (existing.IsPaid) return (null, "Betalda fakturor kan inte uppdateras.");
        if (existing.FiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        existing.SupplierName = invoice.SupplierName;
        existing.InvoiceNumber = invoice.InvoiceNumber;
        existing.InvoiceDate = invoice.InvoiceDate;
        existing.DueDate = invoice.DueDate;
        existing.AmountExclVat = invoice.AmountExclVat;
        existing.VatAmount = invoice.VatAmount;
        existing.TotalAmount = invoice.TotalAmount;
        existing.Notes = invoice.Notes;

        await _db.SaveChangesAsync().ConfigureAwait(false);
        return (existing, null);
    }

    public async Task<(SupplierInvoice? Invoice, string? Error)> CreateAsync(SupplierInvoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.SupplierName))
            return (null, "Leverantörsnamn är obligatoriskt.");
        if (invoice.TotalAmount <= 0)
            return (null, "Totalt belopp måste vara större än noll.");
        if (invoice.DueDate < invoice.InvoiceDate)
            return (null, "Förfallodatum kan inte vara före fakturadatum.");

        var fiscalYear = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == invoice.FiscalYearId).ConfigureAwait(false);
        if (fiscalYear is null) return (null, "Räkenskapsår hittades inte.");
        if (fiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        invoice.CreatedAt = DateTime.UtcNow;
        _db.SupplierInvoices.Add(invoice);
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return (invoice, null);
    }

    public async Task<(SupplierInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId, int expenseAccountId, int payableAccountId, int? vatAccountId)
    {
        var invoice = await _db.SupplierInvoices
            .Include(s => s.FiscalYear)
            .FirstOrDefaultAsync(s => s.Id == invoiceId).ConfigureAwait(false);

        if (invoice is null) return (null, "Fakturan hittades inte.");
        if (invoice.JournalEntryId.HasValue) return (null, "Fakturan är redan bokförd.");
        if (invoice.FiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        if (!await _db.Accounts.AnyAsync(a => a.Id == expenseAccountId && a.FiscalYearId == invoice.FiscalYearId).ConfigureAwait(false))
            return (null, "Kostnadskonto hittades inte.");
        if (!await _db.Accounts.AnyAsync(a => a.Id == payableAccountId && a.FiscalYearId == invoice.FiscalYearId).ConfigureAwait(false))
            return (null, "Skuldkonto hittades inte.");
        if (vatAccountId.HasValue && !await _db.Accounts.AnyAsync(a => a.Id == vatAccountId.Value && a.FiscalYearId == invoice.FiscalYearId).ConfigureAwait(false))
            return (null, "Momskonto hittades inte.");

        var lines = new List<JournalEntryLine>
        {
            new() { AccountId = expenseAccountId, DebitAmount = invoice.AmountExclVat, CreditAmount = 0 }
        };

        if (invoice.VatAmount != 0 && vatAccountId.HasValue)
            lines.Add(new() { AccountId = vatAccountId.Value, DebitAmount = invoice.VatAmount, CreditAmount = 0 });

        lines.Add(new() { AccountId = payableAccountId, DebitAmount = 0, CreditAmount = invoice.TotalAmount });

        // Wrapped in the execution strategy because EnrichNpgsqlDbContext enables a retrying
        // strategy, which refuses user-initiated transactions run outside of it.
        JournalEntry journalEntry = null!;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            using var tx = await _db.Database.BeginTransactionAsync().ConfigureAwait(false);
            var entryNumber = await _db.NextEntryNumberAsync(invoice.FiscalYearId).ConfigureAwait(false);
            journalEntry = new JournalEntry
            {
                EntryNumber = entryNumber,
                Date = invoice.InvoiceDate,
                Description = $"Leverantörsfaktura {invoice.SupplierName}" + (invoice.InvoiceNumber is not null ? $" #{invoice.InvoiceNumber}" : ""),
                FiscalYearId = invoice.FiscalYearId,
                IsPosted = true,
                Status = JournalEntryStatus.Posted,
                CreatedAt = DateTime.UtcNow,
                Lines = lines
            };

            _db.JournalEntries.Add(journalEntry);
            await _db.SaveChangesAsync().ConfigureAwait(false);

            invoice.JournalEntryId = journalEntry.Id;
            await _db.SaveChangesAsync().ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Propagate document links to the new journal entry
        var docs = await _db.Documents
            .Include(d => d.JournalEntries)
            .Where(d => d.SupplierInvoices.Any(s => s.Id == invoiceId))
            .ToListAsync().ConfigureAwait(false);

        foreach (var doc in docs)
            if (!doc.JournalEntries.Any(j => j.Id == journalEntry.Id))
                doc.JournalEntries.Add(journalEntry);

        if (docs.Count > 0)
            await _db.SaveChangesAsync().ConfigureAwait(false);

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
            .ToListAsync().ConfigureAwait(false);
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
            .FirstOrDefaultAsync(s => s.Id == invoiceId).ConfigureAwait(false);

        if (invoice is null) return (null, "Fakturan hittades inte.");
        if (invoice.IsPaid) return (null, "Fakturan är redan betald.");
        if (invoice.FiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        if (!await _db.Accounts.AnyAsync(a => a.Id == bankAccountId && a.FiscalYearId == invoice.FiscalYearId).ConfigureAwait(false))
            return (null, "Bankkonto hittades inte.");
        if (!await _db.Accounts.AnyAsync(a => a.Id == payableAccountId && a.FiscalYearId == invoice.FiscalYearId).ConfigureAwait(false))
            return (null, "Skuldkonto hittades inte.");

        // Wrapped in the execution strategy because EnrichNpgsqlDbContext enables a retrying
        // strategy, which refuses user-initiated transactions run outside of it.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            using var tx = await _db.Database.BeginTransactionAsync().ConfigureAwait(false);
            var entryNumber = await _db.NextEntryNumberAsync(invoice.FiscalYearId).ConfigureAwait(false);
            var paymentEntry = new JournalEntry
            {
                EntryNumber = entryNumber,
                Date = paidDate,
                Description = $"Betalning {invoice.SupplierName}" + (invoice.InvoiceNumber is not null ? $" #{invoice.InvoiceNumber}" : ""),
                FiscalYearId = invoice.FiscalYearId,
                IsPosted = true,
                Status = JournalEntryStatus.Posted,
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
            await _db.SaveChangesAsync().ConfigureAwait(false);

            invoice.PaymentJournalEntryId = paymentEntry.Id;

            if (linkBankTransactionId.HasValue)
            {
                var bankTx = await _db.BankTransactions
                    .FirstOrDefaultAsync(b => b.Id == linkBankTransactionId.Value)
                    .ConfigureAwait(false);
                if (bankTx is not null)
                {
                    bankTx.JournalEntryId = paymentEntry.Id;
                    bankTx.Status = BankTransactionStatus.Matched;
                }
            }

            await _db.SaveChangesAsync().ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        return (invoice, null);
    }

    public async Task<string?> DeleteAsync(int invoiceId)
    {
        var invoice = await _db.SupplierInvoices
            .Include(s => s.FiscalYear)
            .FirstOrDefaultAsync(s => s.Id == invoiceId).ConfigureAwait(false);
        if (invoice is null) return NotFoundMessage;
        if (invoice.JournalEntryId.HasValue) return "Bokförda fakturor kan inte raderas.";
        if (invoice.IsPaid) return "Betalda fakturor kan inte raderas.";
        if (invoice.FiscalYear.IsClosed) return "Räkenskapsåret är stängt.";

        _db.SupplierInvoices.Remove(invoice);
        await _db.SaveChangesAsync().ConfigureAwait(false);
        return null;
    }

    public async Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix)
    {
        return await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId && a.AccountNumber.StartsWith(prefix))
            .OrderBy(a => a.AccountNumber)
            .FirstOrDefaultAsync().ConfigureAwait(false);
    }

    public async Task<List<string>> GetSuppliersAsync(int fiscalYearId)
    {
        return await _db.SupplierInvoices
            .Where(s => s.FiscalYearId == fiscalYearId)
            .Select(s => s.SupplierName)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<HashSet<int>> GetLinkedJournalEntryIdsAsync(int fiscalYearId)
    {
        var ids = await _db.SupplierInvoices
            .Where(s => s.FiscalYearId == fiscalYearId && s.JournalEntryId.HasValue)
            .Select(s => s.JournalEntryId!.Value)
            .ToListAsync().ConfigureAwait(false);
        return [..ids];
    }

    public async Task<List<JournalEntry>> GetLinkableEntriesAsync(int fiscalYearId)
    {
        var linkedIds = await GetLinkedJournalEntryIdsAsync(fiscalYearId).ConfigureAwait(false);
        var entries = await _db.JournalEntries
            .Include(j => j.Lines)
            .Where(j => j.FiscalYearId == fiscalYearId && j.IsPosted && !j.IsClosingEntry)
            .OrderByDescending(j => j.Date)
            .ToListAsync().ConfigureAwait(false);
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

        var fiscalYear = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == invoice.FiscalYearId).ConfigureAwait(false);
        if (fiscalYear is null) return (null, "Räkenskapsår hittades inte.");

        var entry = await _db.JournalEntries.FirstOrDefaultAsync(j => j.Id == journalEntryId).ConfigureAwait(false);
        if (entry is null) return (null, "Verifikationen hittades inte.");
        if (entry.FiscalYearId != invoice.FiscalYearId)
            return (null, "Verifikationen tillhör ett annat räkenskapsår.");

        var alreadyLinked = await _db.SupplierInvoices.AnyAsync(s => s.JournalEntryId == journalEntryId).ConfigureAwait(false);
        if (alreadyLinked) return (null, "Verifikationen är redan kopplad till en faktura.");

        invoice.JournalEntryId = journalEntryId;
        invoice.CreatedAt = DateTime.UtcNow;
        _db.SupplierInvoices.Add(invoice);
        await _db.SaveChangesAsync().ConfigureAwait(false);

        return (invoice, null);
    }

}
