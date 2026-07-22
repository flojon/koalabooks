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
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _customerService;
    private readonly ICurrentUser _currentUser;

    public CustomersController(ICustomerService customerService, ICurrentUser currentUser)
    {
        _customerService = customerService;
        _currentUser = currentUser;
    }

    [HttpGet("customers")]
    [ProducesResponseType<List<CustomerResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAll()
    {
        var customers = await _customerService.GetAllAsync(_currentUser.OrganisationId ?? 0);
        return Ok(customers.Select(MapCustomer).ToList());
    }

    [HttpGet("customers/{id:int}")]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var customer = await _customerService.GetByIdAsync(id);
        if (customer is null) return NotFound();
        return Ok(MapCustomer(customer));
    }

    [HttpPost("customers")]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateCustomerRequest request)
    {
        var customer = new Customer
        {
            OrganisationId = _currentUser.OrganisationId ?? 0,
            Name = request.Name,
            OrgNumber = request.OrgNumber,
            Email = request.Email,
            Phone = request.Phone,
            Address = request.Address,
            PostalCode = request.PostalCode,
            City = request.City,
            Country = request.Country
        };

        var (created, error) = await _customerService.CreateAsync(customer);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = created!.Id }, MapCustomer(created));
    }

    [HttpPut("customers/{id:int}")]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCustomerRequest request)
    {
        var existing = await _customerService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var customer = new Customer
        {
            Id = id,
            Name = request.Name,
            OrgNumber = request.OrgNumber,
            Email = request.Email,
            Phone = request.Phone,
            Address = request.Address,
            PostalCode = request.PostalCode,
            City = request.City,
            Country = request.Country
        };

        var (updated, error) = await _customerService.UpdateAsync(customer);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(MapCustomer(updated!));
    }

    [HttpPost("customers/{id:int}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(int id)
    {
        var existing = await _customerService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var error = await _customerService.DeactivateAsync(id);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    private static CustomerResponse MapCustomer(Customer c) =>
        new(c.Id, c.OrganisationId, c.Name, c.OrgNumber, c.Email, c.Phone,
            c.Address, c.PostalCode, c.City, c.Country, c.IsActive, c.CreatedAt);
}
