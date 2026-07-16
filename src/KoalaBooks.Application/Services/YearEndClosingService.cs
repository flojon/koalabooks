using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public record ClosingValidationResult(bool IsValid, List<string> Errors);

public record ClosingPreview(
    bool IsValid,
    List<string> Errors,
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetResult,
    List<ClosingEntryPreview> Entries);

public record ClosingEntryPreview(string Description, List<ClosingLinePreview> Lines);

public record ClosingLinePreview(string AccountNumber, string AccountName, decimal Debit, decimal Credit);

public record ClosingResult(bool Success, string? Error, int? ClosingEntry1Number, int? ClosingEntry2Number);

public class YearEndClosingService
{
    private readonly AppDbContext _db;
    private readonly IFiscalYearService _fiscalYearService;
    private readonly VoucherGapService _voucherGapService;

    public YearEndClosingService(AppDbContext db, IFiscalYearService fiscalYearService, VoucherGapService voucherGapService)
    {
        _db = db;
        _fiscalYearService = fiscalYearService;
        _voucherGapService = voucherGapService;
    }

    public async Task<ClosingValidationResult> ValidateForClosingAsync(int fiscalYearId)
    {
        var errors = new List<string>();

        var fiscalYear = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == fiscalYearId).ConfigureAwait(false);
        if (fiscalYear is null)
        {
            errors.Add("Fiscal year not found.");
            return new ClosingValidationResult(false, errors);
        }

        if (fiscalYear.IsClosed)
        {
            errors.Add("Fiscal year is already closed.");
        }

        var draftCount = await _db.JournalEntries
            .CountAsync(j => j.FiscalYearId == fiscalYearId && !j.IsPosted).ConfigureAwait(false);
        if (draftCount > 0)
        {
            errors.Add($"Det finns {draftCount} ej bokförda verifikationer. Alla verifikationer måste bokföras innan bokslut.");
        }

        var unexplainedGaps = await _voucherGapService.GetUnexplainedGapsAsync(fiscalYearId).ConfigureAwait(false);
        if (unexplainedGaps.Count > 0)
        {
            errors.Add(
                $"Det finns {unexplainedGaps.Count} lucka/luckor i verifikationsnumreringen (nr {string.Join(", ", unexplainedGaps)}) " +
                "som saknar förklaring enligt BFNAR 2013:2. Ange en förklaring för varje lucka innan bokslutet kan stängas.");
        }

        return new ClosingValidationResult(errors.Count == 0, errors);
    }

    public async Task<ClosingPreview> PreviewClosingAsync(int fiscalYearId)
    {
        var validation = await ValidateForClosingAsync(fiscalYearId).ConfigureAwait(false);
        if (!validation.IsValid)
            return new ClosingPreview(false, validation.Errors, 0, 0, 0, []);

        var fiscalYear = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == fiscalYearId).ConfigureAwait(false);
        if (fiscalYear is null)
            return new ClosingPreview(false, ["Fiscal year not found."], 0, 0, 0, []);

        var plBalances = await GetPnLAccountBalancesAsync(fiscalYearId).ConfigureAwait(false);

        decimal totalRevenue = plBalances
            .Where(a => a.AccountClass == AccountClass.Revenue)
            .Sum(a => a.Balance);
        decimal totalExpenses = plBalances
            .Where(a => a.AccountClass == AccountClass.Expense)
            .Sum(a => a.Balance);
        decimal netResult = totalRevenue - totalExpenses;

        // Look up existing account names for 8999/2099
        var specialAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId && (a.AccountNumber == "8999" || a.AccountNumber == "2099"))
            .ToDictionaryAsync(a => a.AccountNumber).ConfigureAwait(false);
        string name8999 = specialAccounts.GetValueOrDefault("8999")?.Name ?? "Årets resultat";
        string name2099 = specialAccounts.GetValueOrDefault("2099")?.Name ?? "Årets resultat";

        var entries = new List<ClosingEntryPreview>();

        // Entry 1: Close P&L accounts → 8999
        var nonZeroBalances = plBalances.Where(a => a.Balance != 0).ToList();
        if (nonZeroBalances.Count > 0)
        {
            var entry1Lines = new List<ClosingLinePreview>();

            foreach (var acct in nonZeroBalances.OrderBy(a => a.AccountNumber))
            {
                if (acct.AccountClass == AccountClass.Revenue)
                {
                    entry1Lines.Add(acct.Balance > 0
                        ? new ClosingLinePreview(acct.AccountNumber, acct.Name, acct.Balance, 0)
                        : new ClosingLinePreview(acct.AccountNumber, acct.Name, 0, Math.Abs(acct.Balance)));
                }
                else
                {
                    entry1Lines.Add(acct.Balance > 0
                        ? new ClosingLinePreview(acct.AccountNumber, acct.Name, 0, acct.Balance)
                        : new ClosingLinePreview(acct.AccountNumber, acct.Name, Math.Abs(acct.Balance), 0));
                }
            }

            // 8999 balancing line
            var totalDebit = entry1Lines.Sum(l => l.Debit);
            var totalCredit = entry1Lines.Sum(l => l.Credit);
            if (totalDebit > totalCredit)
                entry1Lines.Add(new ClosingLinePreview("8999", name8999, 0, totalDebit - totalCredit));
            else if (totalCredit > totalDebit)
                entry1Lines.Add(new ClosingLinePreview("8999", name8999, totalCredit - totalDebit, 0));

            entries.Add(new ClosingEntryPreview(
                $"Bokslut: Resultatdisposition {fiscalYear.Name}", entry1Lines));
        }

        // Entry 2: 8999 → 2099 (skip if net = 0)
        if (netResult != 0)
        {
            var entry2Lines = new List<ClosingLinePreview>();
            if (netResult > 0)
            {
                entry2Lines.Add(new ClosingLinePreview("8999", name8999, netResult, 0));
                entry2Lines.Add(new ClosingLinePreview("2099", name2099, 0, netResult));
            }
            else
            {
                entry2Lines.Add(new ClosingLinePreview("8999", name8999, 0, Math.Abs(netResult)));
                entry2Lines.Add(new ClosingLinePreview("2099", name2099, Math.Abs(netResult), 0));
            }

            entries.Add(new ClosingEntryPreview(
                $"Bokslut: Årets resultat till eget kapital {fiscalYear.Name}", entry2Lines));
        }

        return new ClosingPreview(true, [], totalRevenue, totalExpenses, netResult, entries);
    }

    public async Task<ClosingResult> ExecuteClosingAsync(int fiscalYearId)
    {
        var validation = await ValidateForClosingAsync(fiscalYearId).ConfigureAwait(false);
        if (!validation.IsValid)
            return new ClosingResult(false, string.Join("; ", validation.Errors), null, null);

        var fiscalYear = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == fiscalYearId).ConfigureAwait(false);
        if (fiscalYear is null)
            return new ClosingResult(false, "Fiscal year not found.", null, null);

        using var transaction = await _db.Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            // Step 1: Auto-create 8999 and 2099 if missing
            var accounts = await _db.Accounts
                .Where(a => a.FiscalYearId == fiscalYearId)
                .ToListAsync().ConfigureAwait(false);

            var account8999 = accounts.FirstOrDefault(a => a.AccountNumber == "8999");
            if (account8999 is null)
            {
                account8999 = new Account
                {
                    AccountNumber = "8999",
                    Name = "Årets resultat",
                    AccountClass = AccountClass.Expense,
                    IsActive = true,
                    IncomingBalance = 0,
                    OutgoingBalance = 0,
                    FiscalYearId = fiscalYearId
                };
                _db.Accounts.Add(account8999);
                accounts.Add(account8999);
            }

            var account2099 = accounts.FirstOrDefault(a => a.AccountNumber == "2099");
            if (account2099 is null)
            {
                account2099 = new Account
                {
                    AccountNumber = "2099",
                    Name = "Årets resultat",
                    AccountClass = AccountClass.Equity,
                    IsActive = true,
                    IncomingBalance = 0,
                    OutgoingBalance = 0,
                    FiscalYearId = fiscalYearId
                };
                _db.Accounts.Add(account2099);
                accounts.Add(account2099);
            }

            // Save to get IDs for auto-created accounts
            await _db.SaveChangesAsync().ConfigureAwait(false);

            // Get transaction totals for all accounts
            var transactionTotals = await _db.JournalEntryLines
                .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId && l.JournalEntry.IsPosted)
                .GroupBy(l => l.AccountId)
                .Select(g => new { AccountId = g.Key, Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
                .ToDictionaryAsync(t => t.AccountId).ConfigureAwait(false);

            // Compute P&L balances (excluding 8999)
            var plAccounts = accounts
                .Where(a => (a.AccountClass == AccountClass.Revenue || a.AccountClass == AccountClass.Expense)
                            && a.AccountNumber != "8999")
                .ToList();

            var plBalances = new List<(Account Account, decimal Balance)>();
            decimal totalRevenue = 0;
            decimal totalExpenses = 0;

            foreach (var account in plAccounts)
            {
                transactionTotals.TryGetValue(account.Id, out var totals);
                decimal debit = totals?.Debit ?? 0;
                decimal credit = totals?.Credit ?? 0;

                decimal balance = account.AccountClass.IsCreditNormal()
                    ? account.IncomingBalance + credit - debit
                    : account.IncomingBalance + debit - credit;

                if (account.AccountClass == AccountClass.Revenue)
                    totalRevenue += balance;
                else
                    totalExpenses += balance;

                if (balance != 0)
                    plBalances.Add((account, balance));
            }

            decimal netResult = totalRevenue - totalExpenses;

            // Step 2: Compute OutgoingBalance for ALL accounts
            foreach (var account in accounts)
            {
                if (account.AccountClass == AccountClass.Revenue || account.AccountClass == AccountClass.Expense)
                {
                    // P&L accounts: UB = 0 after closing
                    account.OutgoingBalance = 0;
                }
                else
                {
                    // Balance sheet accounts: UB from IB + transactions
                    transactionTotals.TryGetValue(account.Id, out var totals);
                    decimal debit = totals?.Debit ?? 0;
                    decimal credit = totals?.Credit ?? 0;

                    account.OutgoingBalance = account.AccountClass.IsCreditNormal()
                        ? account.IncomingBalance + credit - debit
                        : account.IncomingBalance + debit - credit;
                }
            }

            // Adjust 2099 to include the net result from closing entry 2
            account2099.OutgoingBalance += netResult;

            // Get next entry number
            var maxNumber = await _db.JournalEntries
                .Where(j => j.FiscalYearId == fiscalYearId)
                .MaxAsync(j => (int?)j.EntryNumber).ConfigureAwait(false) ?? 0;

            int? entry1Number = null;
            int? entry2Number = null;

            // Step 3: Create closing entry 1 (P&L → 8999)
            if (plBalances.Count > 0)
            {
                maxNumber++;
                entry1Number = maxNumber;

                var entry1 = new JournalEntry
                {
                    EntryNumber = maxNumber,
                    FiscalYearId = fiscalYearId,
                    Date = fiscalYear.EndDate,
                    Description = $"Bokslut: Resultatdisposition {fiscalYear.Name}",
                    CreatedAt = DateTime.UtcNow,
                    IsPosted = true,
                    Status = JournalEntryStatus.Posted,
                    IsClosingEntry = true,
                    Lines = []
                };

                foreach (var (account, balance) in plBalances.OrderBy(pb => pb.Account.AccountNumber))
                {
                    if (account.AccountClass == AccountClass.Revenue)
                    {
                        entry1.Lines.Add(balance > 0
                            ? new JournalEntryLine { AccountId = account.Id, DebitAmount = balance, CreditAmount = 0 }
                            : new JournalEntryLine { AccountId = account.Id, DebitAmount = 0, CreditAmount = Math.Abs(balance) });
                    }
                    else
                    {
                        entry1.Lines.Add(balance > 0
                            ? new JournalEntryLine { AccountId = account.Id, DebitAmount = 0, CreditAmount = balance }
                            : new JournalEntryLine { AccountId = account.Id, DebitAmount = Math.Abs(balance), CreditAmount = 0 });
                    }
                }

                // 8999 balancing line
                var totalDebit = entry1.Lines.Sum(l => l.DebitAmount);
                var totalCredit = entry1.Lines.Sum(l => l.CreditAmount);
                if (totalDebit > totalCredit)
                    entry1.Lines.Add(new JournalEntryLine { AccountId = account8999.Id, DebitAmount = 0, CreditAmount = totalDebit - totalCredit });
                else if (totalCredit > totalDebit)
                    entry1.Lines.Add(new JournalEntryLine { AccountId = account8999.Id, DebitAmount = totalCredit - totalDebit, CreditAmount = 0 });

                _db.JournalEntries.Add(entry1);
            }

            // Step 4: Create closing entry 2 (8999 → 2099), skip if net = 0
            if (netResult != 0)
            {
                maxNumber++;
                entry2Number = maxNumber;

                var entry2 = new JournalEntry
                {
                    EntryNumber = maxNumber,
                    FiscalYearId = fiscalYearId,
                    Date = fiscalYear.EndDate,
                    Description = $"Bokslut: Årets resultat till eget kapital {fiscalYear.Name}",
                    CreatedAt = DateTime.UtcNow,
                    IsPosted = true,
                    Status = JournalEntryStatus.Posted,
                    IsClosingEntry = true,
                    Lines = []
                };

                if (netResult > 0)
                {
                    entry2.Lines.Add(new JournalEntryLine { AccountId = account8999.Id, DebitAmount = netResult, CreditAmount = 0 });
                    entry2.Lines.Add(new JournalEntryLine { AccountId = account2099.Id, DebitAmount = 0, CreditAmount = netResult });
                }
                else
                {
                    entry2.Lines.Add(new JournalEntryLine { AccountId = account8999.Id, DebitAmount = 0, CreditAmount = Math.Abs(netResult) });
                    entry2.Lines.Add(new JournalEntryLine { AccountId = account2099.Id, DebitAmount = Math.Abs(netResult), CreditAmount = 0 });
                }

                _db.JournalEntries.Add(entry2);
            }

            // Step 5: Close the fiscal year
            fiscalYear.IsClosed = true;
            fiscalYear.ClosedAt = DateTime.UtcNow;

            // Save all changes
            await _db.SaveChangesAsync().ConfigureAwait(false);

            // Step 6: Propagate balances to next year if it exists
            await _fiscalYearService.PropagateBalancesToNextYearAsync(fiscalYearId).ConfigureAwait(false);

            await transaction.CommitAsync().ConfigureAwait(false);

            return new ClosingResult(true, null, entry1Number, entry2Number);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            return new ClosingResult(false, $"An error occurred during closing: {ex.Message}", null, null);
        }
    }

    private async Task<List<PnLAccountBalance>> GetPnLAccountBalancesAsync(int fiscalYearId)
    {
        var accounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .Where(a => a.AccountClass == AccountClass.Revenue || a.AccountClass == AccountClass.Expense)
            .Where(a => a.AccountNumber != "8999")
            .ToListAsync().ConfigureAwait(false);

        var accountIds = accounts.Select(a => a.Id).ToHashSet();

        var transactionTotals = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId && l.JournalEntry.IsPosted)
            .Where(l => accountIds.Contains(l.AccountId))
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
            .ToDictionaryAsync(t => t.AccountId).ConfigureAwait(false);

        return accounts.Select(account =>
        {
            transactionTotals.TryGetValue(account.Id, out var totals);
            decimal debit = totals?.Debit ?? 0;
            decimal credit = totals?.Credit ?? 0;

            decimal balance = account.AccountClass.IsCreditNormal()
                ? account.IncomingBalance + credit - debit
                : account.IncomingBalance + debit - credit;

            return new PnLAccountBalance(account.Id, account.AccountNumber, account.Name, account.AccountClass, balance);
        }).ToList();
    }

    private record PnLAccountBalance(int AccountId, string AccountNumber, string Name, AccountClass AccountClass, decimal Balance);
}
