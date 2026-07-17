using KoalaBooks.Application.Services;
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
        return Ok(years.Select(fy => new FiscalYearResponse(
            fy.Id, fy.Name, fy.StartDate, fy.EndDate, fy.IsClosed)).ToList());
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<FiscalYearResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var fy = await _service.GetByIdAsync(id);
        if (fy is null) return NotFound();
        return Ok(new FiscalYearResponse(fy.Id, fy.Name, fy.StartDate, fy.EndDate, fy.IsClosed));
    }

    [HttpGet("active")]
    [ProducesResponseType<FiscalYearResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive()
    {
        var fy = await _service.GetActiveAsync();
        if (fy is null) return NotFound();
        return Ok(new FiscalYearResponse(fy.Id, fy.Name, fy.StartDate, fy.EndDate, fy.IsClosed));
    }
}
