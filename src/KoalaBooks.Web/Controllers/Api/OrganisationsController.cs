using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1/organisation")]
public class OrganisationsController : ControllerBase
{
    private readonly IOrganisationService _organisationService;

    public OrganisationsController(IOrganisationService organisationService)
    {
        _organisationService = organisationService;
    }

    [HttpGet]
    [ProducesResponseType<OrganisationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCurrent()
    {
        var org = await _organisationService.GetCurrentAsync();
        if (org is null) return NotFound();

        return Ok(OrganisationResponse.From(org));
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Update([FromBody] UpdateOrganisationRequest request)
    {
        var error = await _organisationService.UpdateAsync(request.Name, request.OrgNumber);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }
}
