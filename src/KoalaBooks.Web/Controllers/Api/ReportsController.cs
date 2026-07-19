using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class ReportsController : ControllerBase
{
    private readonly IJournalEntryReportingService _reportingService;
    private readonly IFiscalYearService _fiscalYearService;

    public ReportsController(IJournalEntryReportingService reportingService, IFiscalYearService fiscalYearService)
    {
        _reportingService = reportingService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/reports/dashboard-stats")]
    [ProducesResponseType<DashboardStatsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDashboardStats(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var stats = await _reportingService.GetDashboardStatsAsync(fiscalYearId);
        return Ok(new DashboardStatsResponse(stats.EntryCount, stats.TotalDebit, stats.TotalCredit));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/reports/trial-balance")]
    [ProducesResponseType<List<TrialBalanceRowResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTrialBalance(int fiscalYearId, [FromQuery] bool excludeClosingEntries = true)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var rows = await _reportingService.GetTrialBalanceAsync(fiscalYearId, excludeClosingEntries);
        return Ok(rows.Select(r => new TrialBalanceRowResponse(
            r.AccountNumber, r.AccountName, r.AccountClass, r.IncomingBalance, r.TotalDebit, r.TotalCredit, r.Balance)).ToList());
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/reports/balance-sheet")]
    [ProducesResponseType<List<BalanceSheetSectionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBalanceSheet(int fiscalYearId, [FromQuery] bool excludeClosingEntries = false)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var sections = await _reportingService.GetBalanceSheetAsync(fiscalYearId, excludeClosingEntries);
        return Ok(sections.Select(MapBalanceSheetSection).ToList());
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/reports/income-statement")]
    [ProducesResponseType<IncomeStatementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetIncomeStatement(
        int fiscalYearId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] bool excludeClosingEntries = true)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var (sections, netResult) = await _reportingService.GetIncomeStatementAsync(fiscalYearId, from, to, excludeClosingEntries);
        return Ok(new IncomeStatementResponse(
            sections.Select(s => new IncomeStatementSectionResponse(
                s.Title,
                s.Rows.Select(r => new IncomeStatementRowResponse(r.AccountNumber, r.AccountName, r.Amount)).ToList(),
                s.Total)).ToList(),
            netResult));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/reports/vat-report")]
    [ProducesResponseType<VatReportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVatReport(int fiscalYearId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var data = await _reportingService.GetVatReportAsync(fiscalYearId, from, to);
        return Ok(new VatReportResponse(MapVatSection(data.OutputVat), MapVatSection(data.InputVat), data.NetPayable));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/reports/general-ledger")]
    [ProducesResponseType<List<GeneralLedgerAccountSectionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGeneralLedger(
        int fiscalYearId,
        [FromQuery] string? fromAccount,
        [FromQuery] string? toAccount,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] bool excludeClosingEntries = true,
        [FromQuery] bool hideEmpty = false)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var sections = await _reportingService.GetGeneralLedgerAsync(
            fiscalYearId, fromAccount, toAccount, from, to, excludeClosingEntries, hideEmpty);
        return Ok(sections.Select(MapGeneralLedgerSection).ToList());
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/reports/general-ledger/accounts/{accountId:int}")]
    [ProducesResponseType<GeneralLedgerAccountSectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccountLedger(
        int fiscalYearId,
        int accountId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] bool excludeClosingEntries = true)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var section = await _reportingService.GetAccountLedgerAsync(fiscalYearId, accountId, from, to, excludeClosingEntries);
        if (section is null) return NotFound();
        return Ok(MapGeneralLedgerSection(section));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/reports/general-ledger/computed-balances")]
    [ProducesResponseType<List<ComputedBalanceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetComputedBalances(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var balances = await _reportingService.GetComputedBalancesAsync(fiscalYearId);
        return Ok(balances.Select(kv => new ComputedBalanceResponse(kv.Key, kv.Value.IB, kv.Value.UB)).ToList());
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/reports/general-ledger/account-ids-with-transactions")]
    [ProducesResponseType<List<int>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccountIdsWithTransactions(
        int fiscalYearId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] bool includeClosingEntries = false)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var ids = await _reportingService.GetAccountIdsWithTransactionsAsync(fiscalYearId, from, to, includeClosingEntries);
        return Ok(ids.ToList());
    }

    private static BalanceSheetSectionResponse MapBalanceSheetSection(BalanceSheetSection s) =>
        new(s.Title, s.Rows.Select(r => new BalanceSheetRowResponse(
            r.AccountNumber, r.AccountName, r.IncomingBalance, r.PeriodDebit, r.PeriodCredit, r.ClosingBalance)).ToList(), s.Total);

    private static VatReportSectionResponse MapVatSection(VatReportSection s) =>
        new(s.Title, s.Rows.Select(r => new VatReportRowResponse(r.AccountNumber, r.AccountName, r.Debit, r.Credit)).ToList(), s.Total);

    private static GeneralLedgerAccountSectionResponse MapGeneralLedgerSection(GeneralLedgerAccountSection s) =>
        new(s.AccountNumber, s.AccountName, s.IncomingBalance, s.Rows.Select(r => new GeneralLedgerRowResponse(
            r.Date, r.EntryNumber, r.Description, r.DebitAmount, r.CreditAmount, r.RunningBalance)).ToList(), s.ClosingBalance);
}
