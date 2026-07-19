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
public class AccountsController : ControllerBase
{
    private readonly IAccountService _accountService;
    private readonly IFiscalYearService _fiscalYearService;

    public AccountsController(IAccountService accountService, IFiscalYearService fiscalYearService)
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
        return Ok(accounts.Select(MapAccount).ToList());
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

        return Ok(MapAccount(account));
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/accounts")]
    [ProducesResponseType<AccountResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(int fiscalYearId, [FromBody] CreateAccountRequest request)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var account = new Account
        {
            FiscalYearId = fiscalYearId,
            AccountNumber = request.AccountNumber,
            Name = request.Name,
            AccountClass = request.AccountClass!.Value
        };

        var created = await _accountService.CreateAsync(account);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapAccount(created));
    }

    [HttpPut("accounts/{id:int}")]
    [ProducesResponseType<AccountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAccountRequest request)
    {
        var account = await _accountService.GetByIdAsync(id);
        if (account is null) return NotFound();

        // Account has no global query filter — verify tenant ownership via its fiscal year.
        var fy = await _fiscalYearService.GetByIdAsync(account.FiscalYearId);
        if (fy is null) return NotFound();

        account.AccountNumber = request.AccountNumber;
        account.Name = request.Name;
        account.AccountClass = request.AccountClass!.Value;

        await _accountService.UpdateAsync(account);
        return Ok(MapAccount(account));
    }

    [HttpPost("accounts/{id:int}/toggle-active")]
    [ProducesResponseType<AccountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleActive(int id)
    {
        var account = await _accountService.GetByIdAsync(id);
        if (account is null) return NotFound();

        // Account has no global query filter — verify tenant ownership via its fiscal year.
        var fy = await _fiscalYearService.GetByIdAsync(account.FiscalYearId);
        if (fy is null) return NotFound();

        await _accountService.ToggleActiveAsync(id);

        var updated = await _accountService.GetByIdAsync(id);
        return Ok(MapAccount(updated!));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/accounts/missing-from-source/{sourceFiscalYearId:int}")]
    [ProducesResponseType<List<AccountResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMissingFromSource(int fiscalYearId, int sourceFiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var sourceFy = await _fiscalYearService.GetByIdAsync(sourceFiscalYearId);
        if (sourceFy is null) return NotFound();

        var accounts = await _accountService.GetMissingFromSourceAsync(fiscalYearId, sourceFiscalYearId);
        return Ok(accounts.Select(MapAccount).ToList());
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/accounts/copy-accounts")]
    [ProducesResponseType<CountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CopyAccounts(int fiscalYearId, [FromBody] CopyAccountsRequest request)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        // Account has no global query filter — verify tenant ownership of every source
        // account transitively via its fiscal year before copying any of them.
        foreach (var accountId in request.AccountIds.Distinct())
        {
            var source = await _accountService.GetByIdAsync(accountId);
            if (source is null) return NotFound();

            var sourceFy = await _fiscalYearService.GetByIdAsync(source.FiscalYearId);
            if (sourceFy is null) return NotFound();
        }

        var copiedCount = await _accountService.CopyAccountsAsync(fiscalYearId, request.AccountIds);
        return Ok(new CountResponse(copiedCount));
    }

    private static AccountResponse MapAccount(Account a) =>
        new(a.Id, a.AccountNumber, a.Name, a.AccountClass, a.IsActive, a.IncomingBalance, a.OutgoingBalance);
}
