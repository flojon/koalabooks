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
public class CustomerInvoicesController : ControllerBase
{
    private readonly ICustomerInvoiceService _customerInvoiceService;
    private readonly IFiscalYearService _fiscalYearService;

    public CustomerInvoicesController(
        ICustomerInvoiceService customerInvoiceService, IFiscalYearService fiscalYearService)
    {
        _customerInvoiceService = customerInvoiceService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/customer-invoices")]
    [ProducesResponseType<PagedResult<CustomerInvoiceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(
        int fiscalYearId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var all = await _customerInvoiceService.GetAllAsync(fiscalYearId);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(MapInvoice).ToList();

        return Ok(new PagedResult<CustomerInvoiceResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        });
    }

    [HttpGet("customer-invoices/{id:int}")]
    [ProducesResponseType<CustomerInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var invoice = await _customerInvoiceService.GetByIdAsync(id);
        if (invoice is null) return NotFound();
        return Ok(MapInvoice(invoice));
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/customer-invoices")]
    [ProducesResponseType<CustomerInvoiceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(int fiscalYearId, [FromBody] CreateCustomerInvoiceRequest request)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var invoice = new CustomerInvoice
        {
            FiscalYearId = fiscalYearId,
            CustomerId = request.CustomerId,
            CustomerName = request.CustomerName,
            InvoiceDate = request.InvoiceDate!.Value,
            DueDate = request.DueDate!.Value,
            OurReference = request.OurReference,
            YourReference = request.YourReference,
            Notes = request.Notes
        };
        var lines = request.Lines.Select(l => new CustomerInvoiceLine
        {
            Description = l.Description,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            VatRate = l.VatRate
        }).ToList();

        var (created, error) = await _customerInvoiceService.CreateAsync(invoice, lines);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = created!.Id }, MapInvoice(created));
    }

    [HttpPost("customer-invoices/{id:int}/post")]
    [ProducesResponseType<CustomerInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Post(int id, [FromBody] PostCustomerInvoiceRequest request)
    {
        var existing = await _customerInvoiceService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var (posted, error) = await _customerInvoiceService.PostAsync(
            id, request.ReceivableAccountId, request.RevenueAccountId, request.VatRateAccountIds);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(MapInvoice(posted!));
    }

    [HttpPost("customer-invoices/{id:int}/mark-paid")]
    [ProducesResponseType<CustomerInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkPaid(int id, [FromBody] MarkCustomerInvoicePaidRequest request)
    {
        var existing = await _customerInvoiceService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var (paid, error) = await _customerInvoiceService.MarkAsPaidAsync(
            id, request.PaidDate!.Value, request.BankAccountId, request.ReceivableAccountId, request.LinkBankTransactionId);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(MapInvoice(paid!));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/customer-invoices/find-matching-bank-tx")]
    [ProducesResponseType<List<BankTransactionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FindMatchingBankTransactions(
        int fiscalYearId, [FromQuery] decimal invoiceTotal, [FromQuery] DateOnly invoiceDate, [FromQuery] DateOnly dueDate)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var matches = await _customerInvoiceService.FindMatchingBankTransactionsAsync(
            fiscalYearId, invoiceTotal, invoiceDate, dueDate);

        return Ok(matches.Select(b => new BankTransactionResponse(
            b.Id, b.AccountId, b.Account.AccountNumber, b.Date, b.Amount, b.Description, b.Reference, b.Status, b.JournalEntryId)).ToList());
    }

    [HttpDelete("customer-invoices/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var existing = await _customerInvoiceService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var error = await _customerInvoiceService.DeleteAsync(id);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    [HttpGet("customer-invoices/{id:int}/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPdf(int id)
    {
        var bytes = await _customerInvoiceService.GetPdfAsync(id);
        if (bytes is null) return NotFound();
        return File(bytes, "application/pdf");
    }

    private static CustomerInvoiceResponse MapInvoice(CustomerInvoice i) => new(
        i.Id, i.FiscalYearId, i.CustomerId, i.CustomerName, i.CustomerOrgNumber, i.CustomerAddress,
        i.CustomerPostalCode, i.CustomerCity, i.InvoiceNumber, i.InvoiceDate, i.DueDate,
        i.OurReference, i.YourReference, i.Notes,
        i.Lines.Select(l => new CustomerInvoiceLineResponse(
            l.Id, l.Description, l.Quantity, l.UnitPrice, l.VatRate, l.AmountExclVat, l.VatAmount, l.TotalAmount)).ToList(),
        i.AmountExclVat, i.VatAmount, i.TotalAmount, i.IsPosted, i.IsPaid, i.PaidDate,
        i.JournalEntryId, i.PaymentJournalEntryId, i.CreatedAt);
}
