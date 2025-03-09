using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class BusinessController(
    IDataStore dataStore,
    ILogger<BusinessController> logger)
: ControllerBase
{
    [HttpGet("{dbId:guid}")]
    public async Task<IActionResult> GetBusinessAsync(Guid dbId)
    {
        return Ok(await dataStore.GetBusinessAsync(dbId.ToString()));
    }
}
