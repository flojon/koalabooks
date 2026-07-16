using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public record MappingRow(
    string SourceAccountNumber,
    string SourceAccountName,
    decimal Ub,
    string? TargetAccountNumber);

public record ApplyMappingResult(int Mapped, int Skipped);

public class AccountMappingService
{
    private readonly AppDbContext _db;

    public AccountMappingService(AppDbContext db) => _db = db;

    public async Task<List<MappingRow>> BuildMappingAsync(int sourceFiscalYearId, int targetFiscalYearId)
    {
        var sourceYear = await _db.FiscalYears.FindAsync(sourceFiscalYearId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Source fiscal year not found.");

        var sourceAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == sourceFiscalYearId)
            .OrderBy(a => a.AccountNumber)
            .ToListAsync().ConfigureAwait(false);

        var targetAccountNumbers = await _db.Accounts
            .Where(a => a.FiscalYearId == targetFiscalYearId)
            .Select(a => a.AccountNumber)
            .ToHashSetAsync().ConfigureAwait(false);

        Dictionary<int, decimal> effectiveUbs;
        if (sourceYear.IsClosed)
        {
            effectiveUbs = sourceAccounts.ToDictionary(a => a.Id, a => a.OutgoingBalance);
        }
        else
        {
            var sourceAccountIds = sourceAccounts.Select(a => a.Id).ToList();

            var debits = await _db.JournalEntryLines
                .Where(l => sourceAccountIds.Contains(l.AccountId) && l.JournalEntry.IsPosted)
                .GroupBy(l => l.AccountId)
                .Select(g => new { g.Key, Total = g.Sum(l => l.DebitAmount) })
                .ToDictionaryAsync(x => x.Key, x => x.Total).ConfigureAwait(false);

            var credits = await _db.JournalEntryLines
                .Where(l => sourceAccountIds.Contains(l.AccountId) && l.JournalEntry.IsPosted)
                .GroupBy(l => l.AccountId)
                .Select(g => new { g.Key, Total = g.Sum(l => l.CreditAmount) })
                .ToDictionaryAsync(x => x.Key, x => x.Total).ConfigureAwait(false);

            effectiveUbs = sourceAccounts.ToDictionary(a => a.Id, a =>
            {
                var d = debits.GetValueOrDefault(a.Id);
                var c = credits.GetValueOrDefault(a.Id);
                return a.AccountClass.IsCreditNormal()
                    ? a.IncomingBalance + c - d
                    : a.IncomingBalance + d - c;
            });
        }

        return sourceAccounts
            .Select(a => new MappingRow(
                SourceAccountNumber: a.AccountNumber,
                SourceAccountName: a.Name,
                Ub: effectiveUbs[a.Id],
                TargetAccountNumber: targetAccountNumbers.Contains(a.AccountNumber)
                    ? a.AccountNumber : null))
            .Where(r => r.Ub != 0)
            .ToList();
    }

    public async Task<ApplyMappingResult> ApplyMappingAsync(
        int sourceFiscalYearId,
        int targetFiscalYearId,
        List<MappingRow> rows)
    {
        var targetYear = await _db.FiscalYears.FindAsync(targetFiscalYearId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Target fiscal year not found.");

        var targetAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == targetFiscalYearId)
            .ToDictionaryAsync(a => a.AccountNumber).ConfigureAwait(false);

        int mapped = 0, skipped = 0;
        foreach (var row in rows)
        {
            if (row.TargetAccountNumber is null ||
                !targetAccounts.TryGetValue(row.TargetAccountNumber, out var targetAccount))
            {
                skipped++;
                continue;
            }
            targetAccount.IncomingBalance = row.Ub;
            mapped++;
        }

        targetYear.PreviousFiscalYearId = sourceFiscalYearId;
        await _db.SaveChangesAsync().ConfigureAwait(false);

        return new ApplyMappingResult(mapped, skipped);
    }
}
