using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/v1/fiscal-years")]
public class FiscalYearsController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok();

    [HttpGet("{id:int}")]
    public IActionResult GetById(int id) => Ok();
}
