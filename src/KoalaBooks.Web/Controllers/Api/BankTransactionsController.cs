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

    public BankTransactionsController(IBankImportService bankImportService, IFiscalYearService fiscalYearService)
    {
        _bankImportService = bankImportService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/bank-transactions")]
    [ProducesResponseType<PagedResult<BankTransactionResponse>>(StatusCodes.Status200OK)]
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

        return Ok(new PagedResult<BankTransactionResponse>
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

    private static BankTransactionResponse MapTransaction(BankTransaction b) =>
        new(b.Id, b.AccountId, b.Account?.AccountNumber ?? "", b.Date, b.Amount, b.Description,
            b.Reference, b.Status, b.JournalEntryId);
}
