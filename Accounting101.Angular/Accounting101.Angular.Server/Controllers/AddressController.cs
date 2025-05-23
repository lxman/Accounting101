﻿using Accounting101.Angular.DataAccess.Services.Interfaces;
using Accounting101.Angular.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class AddressController(
    IDataStore dataStore,
    IAddressService addressService,
    ILogger<AddressController> logger)
    : ControllerBase
{
    [HttpGet("states")]
    public async Task<ActionResult<List<string>>> GetStatesAsync()
    {
        return Ok(await dataStore.GetStatesAsync());
    }

    [HttpGet("countries")]
    public async Task<ActionResult<List<string>>> GetCountriesAsync()
    {
        return Ok(await dataStore.GetCountriesAsync());
    }

    [HttpPost("{dbId:guid}")]
    public async Task<ActionResult<Guid?>> CreateAddressAsync(Guid dbId)
    {
        return Ok(await addressService.CreateAddressAsync(dbId, Request.Body, dataStore));
    }
}
