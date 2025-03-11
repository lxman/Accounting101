using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class AccountsController(IDataStore dataStore, ILogger<AccountsController> logger) : ControllerBase
{
    [HttpGet("exist/{dbId:guid}/{clientId:guid}")]
    public async Task<IActionResult> AccountsExistAsync(Guid dbId, Guid clientId)
    {
        bool exist = (await dataStore.AccountsForClientAsync(dbId.ToString(), clientId) ?? Array.Empty<AccountWithInfo>()).Any();
        return Ok(exist);
    }

    [HttpPost("{dbId:guid}/{clientId:guid}")]
    public async Task<IActionResult> CreateAccountAsync(Guid dbId, Guid clientId, AccountWithInfo account)
    {
        account.Id = await dataStore.CreateAccountAsync(dbId.ToString(), account);
        return Ok(account.Id);
    }
}
