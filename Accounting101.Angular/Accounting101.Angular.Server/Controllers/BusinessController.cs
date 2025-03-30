using Accounting101.Angular.DataAccess.Models;
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
    public async Task<ActionResult<Business?>> GetBusinessAsync(Guid dbId)
    {
        return Ok(await dataStore.GetBusinessAsync(dbId.ToString()));
    }

    [HttpGet("{dbId:guid}/exists")]
    public async Task<ActionResult<Business?>> BusinessExistsAsync(Guid dbId)
    {
        return Ok(await dataStore.GetBusinessAsync(dbId.ToString()) is not null);
    }

    [HttpPost("{dbId:guid}")]
    public async Task<ActionResult<bool>> CreateBusinessAsync(Guid dbId)
    {
        return await businessService.CreateBusinessAsync(dbId, Request.Body, dataStore)
            ? Ok(true)
            : BadRequest("Failed to create business");
    }

    [HttpDelete("{dbId:guid}")]
    public async Task<IActionResult> DeleteBusinessAsync(Guid dbId)
    {
        await dataStore.DropDatabaseAsync(dbId.ToString());
        return Ok();
    }
}
