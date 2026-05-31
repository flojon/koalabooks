using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/v1")]
public class JournalEntriesController : ControllerBase
{
    [HttpGet("fiscal-years/{fiscalYearId:int}/journal-entries")]
    public IActionResult GetByFiscalYear(int fiscalYearId) => Ok();

    [HttpGet("journal-entries/{id:int}")]
    public IActionResult GetById(int id) => Ok();

    [HttpPost("fiscal-years/{fiscalYearId:int}/journal-entries")]
    public IActionResult Create(int fiscalYearId) => Ok();

    [HttpDelete("journal-entries/{id:int}")]
    public IActionResult Delete(int id) => NoContent();
}
