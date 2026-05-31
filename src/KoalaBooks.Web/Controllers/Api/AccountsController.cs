using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/v1")]
public class AccountsController : ControllerBase
{
    [HttpGet("fiscal-years/{fiscalYearId:int}/accounts")]
    public IActionResult GetByFiscalYear(int fiscalYearId) => Ok();

    [HttpGet("accounts/{id:int}")]
    public IActionResult GetById(int id) => Ok();
}
