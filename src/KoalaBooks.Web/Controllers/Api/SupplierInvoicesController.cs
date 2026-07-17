using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class SupplierInvoicesController : ControllerBase
{
    private readonly ISupplierInvoiceService _supplierInvoiceService;
    private readonly IFiscalYearService _fiscalYearService;

    public SupplierInvoicesController(ISupplierInvoiceService supplierInvoiceService, IFiscalYearService fiscalYearService)
    {
        _supplierInvoiceService = supplierInvoiceService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/supplier-invoices")]
    [ProducesResponseType<PagedResult<SupplierInvoiceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(
        int fiscalYearId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var all = await _supplierInvoiceService.GetAllAsync(fiscalYearId);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(MapInvoice).ToList();

        return Ok(new PagedResult<SupplierInvoiceResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        });
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/supplier-invoices/unpaid-count")]
    [ProducesResponseType<CountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUnpaidCount(int fiscalYearId)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var count = await _supplierInvoiceService.CountUnpaidAsync(fiscalYearId);
        return Ok(new CountResponse(count));
    }

    [HttpGet("supplier-invoices/{id:int}")]
    [ProducesResponseType<SupplierInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var invoice = await _supplierInvoiceService.GetByIdAsync(id);
        if (invoice is null) return NotFound();
        return Ok(MapInvoice(invoice));
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/supplier-invoices")]
    [ProducesResponseType<SupplierInvoiceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(int fiscalYearId, [FromBody] CreateSupplierInvoiceRequest request)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var invoice = new SupplierInvoice
        {
            FiscalYearId = fiscalYearId,
            SupplierName = request.SupplierName,
            InvoiceNumber = request.InvoiceNumber,
            InvoiceDate = request.InvoiceDate!.Value,
            DueDate = request.DueDate!.Value,
            AmountExclVat = request.AmountExclVat,
            VatAmount = request.VatAmount,
            TotalAmount = request.TotalAmount!.Value,
            Notes = request.Notes
        };

        var (created, error) = await _supplierInvoiceService.CreateAsync(invoice);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = created!.Id }, MapInvoice(created));
    }

    [HttpPut("supplier-invoices/{id:int}")]
    [ProducesResponseType<SupplierInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateSupplierInvoiceRequest request)
    {
        var invoice = new SupplierInvoice
        {
            Id = id,
            SupplierName = request.SupplierName,
            InvoiceNumber = request.InvoiceNumber,
            InvoiceDate = request.InvoiceDate!.Value,
            DueDate = request.DueDate!.Value,
            AmountExclVat = request.AmountExclVat,
            VatAmount = request.VatAmount,
            TotalAmount = request.TotalAmount!.Value,
            Notes = request.Notes
        };

        var (updated, error) = await _supplierInvoiceService.UpdateAsync(invoice);
        if (error == SupplierInvoiceService.NotFoundMessage)
            return NotFound();
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(MapInvoice(updated!));
    }

    [HttpDelete("supplier-invoices/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var error = await _supplierInvoiceService.DeleteAsync(id);
        if (error == SupplierInvoiceService.NotFoundMessage)
            return NotFound();
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    private static SupplierInvoiceResponse MapInvoice(SupplierInvoice s) =>
        new(s.Id, s.FiscalYearId, s.SupplierName, s.InvoiceNumber, s.InvoiceDate, s.DueDate,
            s.AmountExclVat, s.VatAmount, s.TotalAmount, s.Notes, s.IsPaid, s.PaidDate,
            s.JournalEntryId, s.PaymentJournalEntryId, s.CreatedAt);
}
