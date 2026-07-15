using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class CustomerInvoiceService
{
    private readonly AppDbContext _db;

    public CustomerInvoiceService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<CustomerInvoice>> GetAllAsync(int fiscalYearId)
    {
        return await _db.CustomerInvoices
            .Include(i => i.Lines)
            .Include(i => i.Customer)
            .Include(i => i.JournalEntry)
            .Include(i => i.PaymentJournalEntry)
            .Where(i => i.FiscalYearId == fiscalYearId)
            .OrderByDescending(i => i.InvoiceNumber)
            .ToListAsync();
    }

    public async Task<CustomerInvoice?> GetByIdAsync(int id)
    {
        return await _db.CustomerInvoices
            .Include(i => i.Lines)
            .Include(i => i.Customer)
            .Include(i => i.FiscalYear).ThenInclude(f => f.Organisation)
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<(CustomerInvoice? Invoice, string? Error)> CreateAsync(
        CustomerInvoice invoice, List<CustomerInvoiceLine> lines)
    {
        if (string.IsNullOrWhiteSpace(invoice.CustomerName))
            return (null, "Kundnamn är obligatoriskt.");
        if (lines.Count == 0)
            return (null, "Fakturan måste ha minst en rad.");
        if (invoice.DueDate < invoice.InvoiceDate)
            return (null, "Förfallodatum kan inte vara före fakturadatum.");

        var fiscalYear = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == invoice.FiscalYearId);
        if (fiscalYear is null) return (null, "Räkenskapsår hittades inte.");
        if (fiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        if (invoice.CustomerId.HasValue)
        {
            var customer = await _db.Customers
                .FirstOrDefaultAsync(c => c.Id == invoice.CustomerId.Value);
            if (customer is not null)
            {
                invoice.CustomerOrgNumber = customer.OrgNumber;
                invoice.CustomerAddress = customer.Address;
                invoice.CustomerPostalCode = customer.PostalCode;
                invoice.CustomerCity = customer.City;
            }
        }

        foreach (var line in lines)
            RecalcLine(line);

        invoice.Lines = lines;
        RecalcTotals(invoice);
        invoice.CreatedAt = DateTime.UtcNow;

        // Advisory lock per fiscal year to serialize invoice number generation,
        // preventing duplicate invoice numbers under concurrent creates. Wrapped in
        // the execution strategy because EnrichNpgsqlDbContext enables a retrying
        // strategy, which refuses user-initiated transactions run outside of it.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            using var tx = await _db.Database.BeginTransactionAsync();
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(42000 + {0})", invoice.FiscalYearId);
            invoice.InvoiceNumber = await NextInvoiceNumberAsync(invoice.FiscalYearId);

            _db.CustomerInvoices.Add(invoice);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        return (invoice, null);
    }

    public async Task<(CustomerInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId,
        int receivableAccountId,
        int revenueAccountId,
        IReadOnlyDictionary<int, int> vatRateAccountIds)
    {
        var invoice = await _db.CustomerInvoices
            .Include(i => i.Lines)
            .Include(i => i.FiscalYear)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (invoice is null) return (null, "Fakturan hittades inte.");
        if (invoice.IsPosted) return (null, "Fakturan är redan bokförd.");
        if (invoice.FiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        // Validate that all account IDs belong to this invoice's fiscal year to prevent cross-tenant writes.
        var accountIds = new List<int> { receivableAccountId, revenueAccountId };
        accountIds.AddRange(vatRateAccountIds.Values.Where(id => id != 0));
        var validCount = await _db.Accounts
            .CountAsync(a => accountIds.Contains(a.Id) && a.FiscalYearId == invoice.FiscalYearId);
        if (validCount != accountIds.Distinct().Count())
            return (null, "Ett eller flera konton tillhör inte detta räkenskapsår.");

        var journalLines = new List<JournalEntryLine>
        {
            new() { AccountId = receivableAccountId, DebitAmount = invoice.TotalAmount, CreditAmount = 0 }
        };

        // One credit line per VAT rate so each maps to its BAS account (2610/2620/2625).
        var vatByRate = invoice.Lines
            .Where(l => l.VatRate > 0)
            .GroupBy(l => l.VatRate)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.VatAmount));

        foreach (var (rate, amount) in vatByRate)
        {
            if (vatRateAccountIds.TryGetValue(rate, out var vatAccountId) && vatAccountId != 0)
                journalLines.Add(new() { AccountId = vatAccountId, DebitAmount = 0, CreditAmount = amount });
        }

        journalLines.Add(new() { AccountId = revenueAccountId, DebitAmount = 0, CreditAmount = invoice.AmountExclVat });

        using var tx = await _db.Database.BeginTransactionAsync();
        var entryNumber = await _db.NextEntryNumberAsync(invoice.FiscalYearId);
        var journalEntry = new JournalEntry
        {
            EntryNumber = entryNumber,
            Date = invoice.InvoiceDate,
            Description = $"Kundfaktura {invoice.CustomerName} #{invoice.InvoiceNumber}",
            FiscalYearId = invoice.FiscalYearId,
            IsPosted = true,
            Status = JournalEntryStatus.Posted,
            CreatedAt = DateTime.UtcNow,
            Lines = journalLines
        };

        _db.JournalEntries.Add(journalEntry);
        invoice.IsPosted = true;
        invoice.JournalEntry = journalEntry;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        // Propagate document links to the new journal entry
        var docs = await _db.Documents
            .Include(d => d.JournalEntries)
            .Where(d => d.CustomerInvoices.Any(c => c.Id == invoiceId))
            .ToListAsync();

        foreach (var doc in docs)
            if (!doc.JournalEntries.Any(j => j.Id == journalEntry.Id))
                doc.JournalEntries.Add(journalEntry);

        if (docs.Count > 0)
            await _db.SaveChangesAsync();

        return (invoice, null);
    }

    public async Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate)
    {
        var minDate = invoiceDate.AddDays(-3);
        var maxDate = dueDate.AddDays(60);

        return await _db.BankTransactions
            .Include(b => b.Account)
            .Where(b => b.Account.FiscalYearId == fiscalYearId)
            .Where(b => b.Status == BankTransactionStatus.Unmatched)
            .Where(b => b.Date >= minDate && b.Date <= maxDate)
            .Where(b => b.Amount >= invoiceTotal - 0.01m && b.Amount <= invoiceTotal + 0.01m)
            .OrderBy(b => b.Date)
            .ToListAsync();
    }

    public async Task<(CustomerInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId,
        DateOnly paidDate,
        int bankAccountId,
        int receivableAccountId,
        int? linkBankTransactionId = null)
    {
        var invoice = await _db.CustomerInvoices
            .Include(i => i.FiscalYear)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (invoice is null) return (null, "Fakturan hittades inte.");
        if (invoice.IsPaid) return (null, "Fakturan är redan betald.");
        if (!invoice.IsPosted) return (null, "Fakturan måste bokföras innan betalning registreras.");
        if (invoice.FiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

        var validCount = await _db.Accounts
            .CountAsync(a => (a.Id == bankAccountId || a.Id == receivableAccountId)
                          && a.FiscalYearId == invoice.FiscalYearId);
        if (validCount < 2)
            return (null, "Ett eller flera konton tillhör inte detta räkenskapsår.");

        using var tx = await _db.Database.BeginTransactionAsync();
        var entryNumber = await _db.NextEntryNumberAsync(invoice.FiscalYearId);
        var paymentEntry = new JournalEntry
        {
            EntryNumber = entryNumber,
            Date = paidDate,
            Description = $"Inbetalning {invoice.CustomerName} #{invoice.InvoiceNumber}",
            FiscalYearId = invoice.FiscalYearId,
            IsPosted = true,
            Status = JournalEntryStatus.Posted,
            CreatedAt = DateTime.UtcNow,
            Lines =
            [
                new() { AccountId = bankAccountId,        DebitAmount = invoice.TotalAmount, CreditAmount = 0 },
                new() { AccountId = receivableAccountId,  DebitAmount = 0, CreditAmount = invoice.TotalAmount }
            ]
        };

        _db.JournalEntries.Add(paymentEntry);
        invoice.IsPaid = true;
        invoice.PaidDate = paidDate;
        invoice.PaymentJournalEntry = paymentEntry;

        if (linkBankTransactionId.HasValue)
        {
            var bankTx = await _db.BankTransactions.FirstOrDefaultAsync(b => b.Id == linkBankTransactionId.Value);
            if (bankTx is not null)
            {
                bankTx.JournalEntry = paymentEntry;
                bankTx.Status = BankTransactionStatus.Matched;
            }
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return (invoice, null);
    }

    public async Task<string?> DeleteAsync(int invoiceId)
    {
        var invoice = await _db.CustomerInvoices
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice is null) return "Fakturan hittades inte.";
        if (invoice.IsPosted) return "Bokförda fakturor kan inte raderas.";
        if (invoice.IsPaid) return "Betalda fakturor kan inte raderas.";

        _db.CustomerInvoices.Remove(invoice);
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

    public static void RecalcLine(CustomerInvoiceLine line)
    {
        line.AmountExclVat = Math.Round(line.Quantity * line.UnitPrice, 2);
        line.VatAmount = Math.Round(line.AmountExclVat * line.VatRate / 100m, 2);
        line.TotalAmount = line.AmountExclVat + line.VatAmount;
    }

    private static void RecalcTotals(CustomerInvoice invoice)
    {
        invoice.AmountExclVat = invoice.Lines.Sum(l => l.AmountExclVat);
        invoice.VatAmount = invoice.Lines.Sum(l => l.VatAmount);
        invoice.TotalAmount = invoice.Lines.Sum(l => l.TotalAmount);
    }

    private async Task<int> NextInvoiceNumberAsync(int fiscalYearId)
    {
        return (await _db.CustomerInvoices
            .Where(i => i.FiscalYearId == fiscalYearId)
            .MaxAsync(i => (int?)i.InvoiceNumber) ?? 0) + 1;
    }

}
