using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RootGroup = Accounting101.Angular.DataAccess.AccountGroups.RootGroup;

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

    [HttpGet("{dbId:guid}/{clientId:guid}/layout")]
    public async Task<IActionResult> GetAccountsLayoutAsync(Guid dbId, Guid clientId)
    {
        RootGroup layout = (await dataStore.GetRootGroupAsync(dbId.ToString(), clientId));
        return Ok(layout);
    }

    [HttpPost("{dbId:guid}/{clientId:guid}/layout")]
    public async Task<IActionResult> SaveAccountsLayoutAsync(Guid dbId, Guid clientId, RootGroup layout)
    {
        await dataStore.SaveRootGroupAsync(dbId.ToString(), clientId, layout);
        return Ok();
    }

    [HttpPost("{dbId:guid}/{clientId:guid}")]
    public async Task<IActionResult> CreateAccountAsync(Guid dbId, Guid clientId, AccountWithInfo account)
    {
        account.Id = await dataStore.CreateAccountAsync(dbId.ToString(), account);
        return Ok(account.Id);
    }
}
