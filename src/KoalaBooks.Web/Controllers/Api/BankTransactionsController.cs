using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class BankTransactionsController : ControllerBase
{
    private readonly IBankImportService _bankImportService;
    private readonly IFiscalYearService _fiscalYearService;
    private readonly IAccountService _accountService;

    public BankTransactionsController(
        IBankImportService bankImportService, IFiscalYearService fiscalYearService, IAccountService accountService)
    {
        _bankImportService = bankImportService;
        _fiscalYearService = fiscalYearService;
        _accountService = accountService;
    }

    [HttpGet("bank-transactions/unmatched-count")]
    [ProducesResponseType<CountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUnmatchedCountForOrganisation()
    {
        var count = await _bankImportService.CountUnmatchedForOrganisationAsync();
        return Ok(new CountResponse(count));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/bank-transactions")]
    [ProducesResponseType<KoalaBooks.Web.Models.Api.PagedResult<BankTransactionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(
        int fiscalYearId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? accountId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var all = await _bankImportService.GetByFiscalYearAsync(fiscalYearId, from, to, accountId);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(MapTransaction).ToList();

        return Ok(new KoalaBooks.Web.Models.Api.PagedResult<BankTransactionResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        });
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/bank-transactions/unmatched-count")]
    [ProducesResponseType<CountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUnmatchedCount(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var count = await _bankImportService.CountUnmatchedAsync(fiscalYearId);
        return Ok(new CountResponse(count));
    }

    [HttpGet("bank-transactions/{id:int}")]
    [ProducesResponseType<BankTransactionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var tx = await _bankImportService.GetByIdAsync(id);
        if (tx is null) return NotFound();
        return Ok(MapTransaction(tx));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/bank-transactions/unmatched")]
    [ProducesResponseType<List<BankTransactionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUnmatched(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var txs = await _bankImportService.GetUnmatchedAsync(fiscalYearId);
        return Ok(txs.Select(MapTransaction).ToList());
    }

    [HttpPost("accounts/{accountId:int}/bank-transactions/parse-preview")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ParsePreviewResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ParsePreview(int accountId, [FromForm] ParsePreviewRequest request)
    {
        var account = await _accountService.GetByIdAsync(accountId);
        if (account is null) return NotFound();

        // Account has no global query filter — verify tenant ownership via its fiscal year.
        var fy = await _fiscalYearService.GetByIdAsync(account.FiscalYearId);
        if (fy is null) return NotFound();

        await using var stream = request.File!.OpenReadStream();
        var parseResult = _bankImportService.ParseFile(stream, request.File.FileName);
        if (!parseResult.Success)
            return Ok(new ParsePreviewResponse(false, parseResult.Error, parseResult.Headers, []));

        var previews = await _bankImportService.BuildPreviewAsync(
            accountId, parseResult.DataRows,
            request.DateCol!.Value, request.AmountCol!.Value, request.DescCol!.Value, request.RefCol,
            request.DateFormat!);

        return Ok(new ParsePreviewResponse(true, null, parseResult.Headers,
            previews.Select(p => new BankTransactionPreviewResponse(
                p.RowIndex, p.Date, p.Amount, p.Description, p.Reference, p.IsDuplicate, p.ParseError)).ToList()));
    }

    [HttpPost("accounts/{accountId:int}/bank-transactions/import")]
    [ProducesResponseType<ImportBankTransactionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Import(int accountId, [FromBody] ImportBankTransactionsRequest request)
    {
        var account = await _accountService.GetByIdAsync(accountId);
        if (account is null) return NotFound();

        // Account has no global query filter — verify tenant ownership via its fiscal year.
        var fy = await _fiscalYearService.GetByIdAsync(account.FiscalYearId);
        if (fy is null) return NotFound();

        var previews = request.Transactions.Select(t => new BankTransactionPreview(
            t.RowIndex, t.Date, t.Amount, t.Description, t.Reference, t.IsDuplicate, t.ParseError)).ToList();

        var result = await _bankImportService.ImportAsync(accountId, previews);
        return Ok(new ImportBankTransactionsResponse(result.Imported, result.Skipped, result.Duplicates, result.Errors));
    }

    [HttpPost("accounts/{accountId:int}/bank-transactions/suggest-contra")]
    [ProducesResponseType<SuggestContraAccountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuggestContra(int accountId, [FromBody] SuggestContraAccountRequest request)
    {
        var account = await _accountService.GetByIdAsync(accountId);
        if (account is null) return NotFound();

        // Account has no global query filter — verify tenant ownership via its fiscal year.
        var fy = await _fiscalYearService.GetByIdAsync(account.FiscalYearId);
        if (fy is null) return NotFound();

        var suggested = await _bankImportService.SuggestContraAccountAsync(accountId, request.Description, request.Amount!.Value);
        return Ok(new SuggestContraAccountResponse(suggested));
    }

    [HttpPost("bank-transactions/{id:int}/set-status")]
    [ProducesResponseType<BankTransactionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetBankTransactionStatusRequest request)
    {
        var tx = await _bankImportService.GetByIdAsync(id);
        if (tx is null) return NotFound();

        await _bankImportService.SetStatusAsync(id, request.Status!.Value);

        var updated = await _bankImportService.GetByIdAsync(id);
        return Ok(MapTransaction(updated!));
    }

    [HttpPost("bank-transactions/{id:int}/match-to-entry")]
    [ProducesResponseType<BankTransactionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MatchToEntry(int id, [FromBody] MatchToEntryRequest request)
    {
        var tx = await _bankImportService.GetByIdAsync(id);
        if (tx is null) return NotFound();

        var error = await _bankImportService.MatchToEntryAsync(id, request.JournalEntryId!.Value);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        var updated = await _bankImportService.GetByIdAsync(id);
        return Ok(MapTransaction(updated!));
    }

    private static BankTransactionResponse MapTransaction(BankTransaction b) =>
        new(b.Id, b.AccountId, b.Account?.AccountNumber ?? "", b.Account?.Name ?? "", b.Date, b.Amount, b.Description,
            b.Reference, b.Status, b.JournalEntryId);
}
