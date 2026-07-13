using KoalaBooks.Application.Services;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class AccountsController : ControllerBase
{
    private readonly IAccountService _accountService;
    private readonly FiscalYearService _fiscalYearService;

    public AccountsController(IAccountService accountService, FiscalYearService fiscalYearService)
    {
        _accountService = accountService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/accounts")]
    [ProducesResponseType<List<AccountResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(int fiscalYearId)
    {
        // FiscalYearService.GetByIdAsync uses the global query filter — returns null if
        // the fiscal year doesn't belong to the current tenant.
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var accounts = await _accountService.GetAllAsync(fiscalYearId);
        return Ok(accounts.Select(a => new AccountResponse(
            a.Id, a.AccountNumber, a.Name, a.AccountClass,
            a.IsActive, a.IncomingBalance, a.OutgoingBalance)).ToList());
    }

    [HttpGet("accounts/{id:int}")]
    [ProducesResponseType<AccountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var account = await _accountService.GetByIdAsync(id);
        if (account is null) return NotFound();

        // Account has no global query filter — verify tenant ownership via its fiscal year.
        var fy = await _fiscalYearService.GetByIdAsync(account.FiscalYearId);
        if (fy is null) return NotFound();

        return Ok(new AccountResponse(
            account.Id, account.AccountNumber, account.Name, account.AccountClass,
            account.IsActive, account.IncomingBalance, account.OutgoingBalance));
    }
}
