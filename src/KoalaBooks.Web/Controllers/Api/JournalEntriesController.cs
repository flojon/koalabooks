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
public class JournalEntriesController : ControllerBase
{
    private readonly JournalEntryService _journalEntryService;
    private readonly FiscalYearService _fiscalYearService;

    public JournalEntriesController(JournalEntryService journalEntryService, FiscalYearService fiscalYearService)
    {
        _journalEntryService = journalEntryService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/journal-entries")]
    [ProducesResponseType<PagedResult<JournalEntryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(
        int fiscalYearId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var all = await _journalEntryService.GetByFiscalYearAsync(fiscalYearId, from, to);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(MapEntry).ToList();

        return Ok(new PagedResult<JournalEntryResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        });
    }

    [HttpGet("journal-entries/{id:int}")]
    [ProducesResponseType<JournalEntryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var entry = await _journalEntryService.GetByIdAsync(id);
        if (entry is null) return NotFound();
        return Ok(MapEntry(entry));
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/journal-entries")]
    [ProducesResponseType<JournalEntryResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(int fiscalYearId, [FromBody] CreateJournalEntryRequest request)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var entry = new JournalEntry
        {
            FiscalYearId = fiscalYearId,
            Date = request.Date,
            Description = request.Description,
            Lines = request.Lines.Select(l => new JournalEntryLine
            {
                AccountId = l.AccountId,
                DebitAmount = l.DebitAmount,
                CreditAmount = l.CreditAmount
            }).ToList()
        };

        var (created, error) = await _journalEntryService.CreateAsync(entry);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = created!.Id }, MapEntry(created));
    }

    [HttpDelete("journal-entries/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var entry = await _journalEntryService.GetByIdAsync(id);
        if (entry is null) return NotFound();

        var error = await _journalEntryService.DeleteDraftAsync(id);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    private static JournalEntryResponse MapEntry(JournalEntry e) =>
        new(e.Id, e.EntryNumber, e.Date, e.Description, e.IsPosted, e.CreatedAt,
            e.Lines.Select(l => new JournalEntryLineResponse(
                l.Id, l.AccountId,
                l.Account?.AccountNumber ?? "",
                l.Account?.Name ?? "",
                l.DebitAmount, l.CreditAmount)).ToList());
}
