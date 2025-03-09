using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.Interfaces;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]

public class AddressController(IDataStore dataStore, ILogger<AddressController> logger) : ControllerBase
{
    [HttpGet("states")]
    public async Task<IActionResult> GetStatesAsync()
    {
        return Ok(await dataStore.GetStatesAsync());
    }

    [HttpGet("countries")]
    public async Task<IActionResult> GetCountriesAsync()
    {
        return Ok(await dataStore.GetCountriesAsync());
    }

    [HttpPost("{dbId:guid}")]
    public async Task<IActionResult> CreateAddressAsync(Guid dbId, [FromBody] IAddress address)
    {
        return Ok(await dataStore.CreateAddressAsync(dbId.ToString(), address));
    }
}
