using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class ClientsController(
    IDataStore dataStore,
    ILogger<ClientsController> logger)
    : ControllerBase
{
    [HttpGet("exist/{dbId:guid}")]
    public async Task<IActionResult> ClientsExistAsync(Guid dbId)
    {
        bool exist = await dataStore.ClientsExistAsync(dbId.ToString());
        return Ok(exist);
    }

    [HttpGet("{dbId:guid}")]
    public async Task<IActionResult> GetClientsAsync(Guid dbId)
    {
        return Ok(await dataStore.AllClientsWithInfosAsync(dbId.ToString()));
    }

    [HttpPost("{dbId:guid}")]
    public async Task<IActionResult> CreateClientAsync(Guid dbId, [FromBody] Client client)
    {
        if (await dataStore.CreateClientAsync(dbId.ToString(), client) != Guid.Empty)
        {
            return Ok(client);
        }
        return BadRequest();
    }
}
