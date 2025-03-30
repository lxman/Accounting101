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
    [HttpGet("{dbId:guid}/exist")]
    public async Task<ActionResult<bool>> ClientsExistAsync(Guid dbId)
    {
        bool exist = await dataStore.ClientsExistAsync(dbId.ToString());
        return Ok(exist);
    }

    [HttpGet("{dbId:guid}")]
    public async Task<ActionResult<IEnumerable<ClientWithInfo>?>> GetClientsAsync(Guid dbId)
    {
        return Ok(await dataStore.AllClientsWithInfosAsync(dbId.ToString()));
    }

    [HttpGet("{dbId:guid}/{clientId}")]
    public async Task<ActionResult<ClientWithInfo?>> GetClientAsync(Guid dbId, string clientId)
    {
        ClientWithInfo? client = await dataStore.GetClientWithInfoAsync(dbId.ToString(), clientId);
        return client is null
            ? NotFound()
            : Ok(client);
    }

    [HttpPost("{dbId:guid}")]
    public async Task<ActionResult<Client>> CreateClientAsync(Guid dbId, [FromBody] Client client)
    {
        if (await dataStore.CreateClientAsync(dbId.ToString(), client) != Guid.Empty)
        {
            return Ok(client);
        }
        return BadRequest();
    }

    [HttpDelete("{dbId:guid}/{clientId}")]
    public async Task<ActionResult<bool?>> DeleteClientAsync(Guid dbId, string clientId)
    {
        return Ok(await dataStore.DeleteClientAsync(dbId.ToString(), clientId));
    }
}
