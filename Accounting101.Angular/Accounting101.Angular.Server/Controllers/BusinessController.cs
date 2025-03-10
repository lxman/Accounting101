using Accounting101.Angular.DataAccess.Services.Interfaces;
using Accounting101.Angular.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class BusinessController(
    IDataStore dataStore,
    IBusinessService businessService,
    ILogger<BusinessController> logger)
: ControllerBase
{
    [HttpGet("{dbId:guid}")]
    public async Task<IActionResult> GetBusinessAsync(Guid dbId)
    {
        return Ok(await dataStore.GetBusinessAsync(dbId.ToString()));
    }

    [HttpGet("exists/{dbId:guid}")]
    public async Task<IActionResult> BusinessExistsAsync(Guid dbId)
    {
        return Ok(await dataStore.GetBusinessAsync(dbId.ToString()) is not null);
    }

    [HttpPost("{dbId:guid}")]
    public async Task<IActionResult> CreateBusinessAsync(Guid dbId)
    {
        return await businessService.CreateBusinessAsync(dbId, Request.Body, dataStore)
            ? Ok()
            : BadRequest("Failed to create business");
    }
}
