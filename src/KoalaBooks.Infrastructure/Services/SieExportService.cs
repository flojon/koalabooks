using System.Globalization;
using System.Text;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Services;

public class SieExportService
{
    private readonly AppDbContext _db;

    public SieExportService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<byte[]> ExportAsync(int fiscalYearId, string? companyName = null)
    {
        var fiscalYear = await _db.FiscalYears
            .Include(f => f.Accounts)
            .Include(f => f.JournalEntries)
                .ThenInclude(j => j.Lines)
                    .ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(f => f.Id == fiscalYearId)
            ?? throw new InvalidOperationException($"Fiscal year {fiscalYearId} not found.");

        var sb = new StringBuilder();

        sb.AppendLine("#FLAGGA 0");
        sb.AppendLine("#FORMAT PC8");
        sb.AppendLine("#SIETYP 4");
        sb.AppendLine("#PROGRAM \"KoalaBooks\" 1.0");
        sb.AppendLine($"#GEN {DateTime.Today:yyyyMMdd}");
        sb.AppendLine($"#FNAMN \"{companyName ?? fiscalYear.Name}\"");
        sb.AppendLine($"#RAR 0 {fiscalYear.StartDate:yyyyMMdd} {fiscalYear.EndDate:yyyyMMdd}");

        foreach (var account in fiscalYear.Accounts.OrderBy(a => a.AccountNumber))
        {
            sb.AppendLine($"#KONTO {account.AccountNumber} \"{account.Name}\"");
        }

        foreach (var account in fiscalYear.Accounts.OrderBy(a => a.AccountNumber))
        {
            if (account.IncomingBalance != 0)
            {
                sb.AppendLine($"#IB 0 {account.AccountNumber} {FormatAmount(account.IncomingBalance)}");
            }
        }

        foreach (var account in fiscalYear.Accounts.OrderBy(a => a.AccountNumber))
        {
            if (account.OutgoingBalance != 0)
            {
                sb.AppendLine($"#UB 0 {account.AccountNumber} {FormatAmount(account.OutgoingBalance)}");
            }
        }

        foreach (var entry in fiscalYear.JournalEntries.Where(e => e.IsPosted).OrderBy(e => e.EntryNumber))
        {
            var dateStr = entry.Date.ToString("yyyyMMdd");
            sb.AppendLine($"#VER \"\" {entry.EntryNumber} {dateStr} \"{entry.Description}\"");
            sb.AppendLine("{");
            foreach (var line in entry.Lines)
            {
                var amount = line.DebitAmount > 0 ? line.DebitAmount : -line.CreditAmount;
                var lineDate = entry.Date.ToString("yyyyMMdd");
                sb.AppendLine($"    #TRANS {line.Account.AccountNumber} {{}} {FormatAmount(amount)} {lineDate} \"\"");
            }
            sb.AppendLine("}");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp437 = Encoding.GetEncoding(437);
        return cp437.GetBytes(sb.ToString());
    }

    private static string FormatAmount(decimal amount)
    {
        return amount.ToString("F2", CultureInfo.InvariantCulture);
    }
}
