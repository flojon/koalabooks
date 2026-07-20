using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1/fiscal-years")]
public class FiscalYearsController : ControllerBase
{
    private readonly IFiscalYearService _service;

    public FiscalYearsController(IFiscalYearService service) => _service = service;

    [HttpGet]
    [ProducesResponseType<List<FiscalYearResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAll()
    {
        var years = await _service.GetAllAsync();
        return Ok(years.Select(MapFiscalYear).ToList());
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<FiscalYearResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var fy = await _service.GetByIdAsync(id);
        if (fy is null) return NotFound();
        return Ok(MapFiscalYear(fy));
    }

    [HttpGet("default")]
    [ProducesResponseType<FiscalYearResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDefault()
    {
        var fy = await _service.GetDefaultFiscalYearAsync();
        if (fy is null) return NotFound();
        return Ok(MapFiscalYear(fy));
    }

    [HttpPost]
    [ProducesResponseType<FiscalYearResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateFiscalYearRequest request)
    {
        var fiscalYear = new FiscalYear
        {
            Name = request.Name,
            StartDate = request.StartDate!.Value,
            EndDate = request.EndDate!.Value
        };

        var (created, error) = await _service.CreateAsync(fiscalYear);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = created!.Id }, MapFiscalYear(created));
    }

    [HttpGet("{id:int}/accounts-for-year")]
    [ProducesResponseType<List<AccountResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccountsForYear(int id)
    {
        var fy = await _service.GetByIdAsync(id);
        if (fy is null) return NotFound();

        var accounts = await _service.GetAccountsAsync(id);
        return Ok(accounts.Select(AccountResponse.From).ToList());
    }

    [HttpPost("{id:int}/propagate-balances")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PropagateBalances(int id)
    {
        var fy = await _service.GetByIdAsync(id);
        if (fy is null) return NotFound();

        await _service.PropagateBalancesToNextYearAsync(id);
        return NoContent();
    }

    private static FiscalYearResponse MapFiscalYear(FiscalYear fy) =>
        new(fy.Id, fy.Name, fy.StartDate, fy.EndDate, fy.IsClosed);
}
