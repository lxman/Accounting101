using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.AccountGroups;
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
    [HttpGet("{dbId:guid}/{clientId}/exist")]
    public async Task<ActionResult<bool>> AccountsExistAsync(Guid dbId, string clientId)
    {
        bool exist = (await dataStore.AccountsForClientAsync(dbId.ToString(), clientId) ?? Array.Empty<AccountWithInfo>()).Any();
        return Ok(exist);
    }

    [HttpGet("{dbId:guid}/{clientId}/layout")]
    public async Task<ActionResult<RootGroup>> GetAccountsLayoutAsync(Guid dbId, string clientId)
    {
        RootGroup layout = (await dataStore.GetRootGroupAsync(dbId.ToString(), clientId));
        return Ok(layout);
    }

    [HttpGet("{dbId:guid}/{clientId}")]
    public async Task<ActionResult<List<AccountWithInfo>>> GetAccountsAsync(Guid dbId, string clientId)
    {
        List<AccountWithInfo> accounts = (await dataStore.AccountsForClientAsync(dbId.ToString(), clientId))?.ToList() ?? [];
        return Ok(accounts);
    }

    [HttpGet("{dbId:guid}/{accountId}/transactions")]
    public ActionResult<List<Transaction>> TransactionsForAccount(Guid dbId, string accountId)
    {
        return Ok(dataStore.TransactionsForAccount(dbId.ToString(), accountId));
    }

    [HttpPost("{dbId:guid}/{accountId}/balance")]
    public async Task<ActionResult<decimal>> GetAccountBalanceOnDateAsync(Guid dbId, string accountId, [FromBody] DateTimeRequest date)
    {
        return Ok(await dataStore.GetAccountBalanceOnDateAsync(dbId.ToString(), accountId, DateOnly.FromDateTime(DateTime.Parse(date.Date))));
    }

    [HttpPost("{dbId:guid}/{clientId}/transactions")]
    public async Task<ActionResult<Guid>> CreateTransactionAsync(Guid dbId, string clientId, [FromBody] Transaction? transaction)
    {
        return transaction is not null
            ? Ok(await dataStore.CreateTransactionAsync(dbId.ToString(), clientId, transaction))
            : BadRequest();
    }

    [HttpPost("{dbId:guid}/{clientId}/layout")]
    public async Task<ActionResult<bool>> SaveAccountsLayoutAsync(Guid dbId, string clientId, [FromBody] RootGroup layout)
    {
        bool result = await dataStore.SaveRootGroupAsync(dbId.ToString(), clientId, layout);
        return Ok(result);
    }

    [HttpPost("{dbId:guid}/{clientId:guid}")]
    public async Task<ActionResult<Guid>> CreateAccountAsync(Guid dbId, Guid clientId, [FromBody] AccountWithInfo account)
    {
        account.Id = await dataStore.CreateAccountAsync(dbId.ToString(), account);
        return Ok(account.Id);
    }

    [HttpPut("{dbId:guid}/{clientId}/transactions")]
    public async Task<ActionResult<bool>> UpdateTransactionAsync(Guid dbId, string clientId, [FromBody] Transaction? transaction)
    {
        return transaction is not null
            ? Ok(await dataStore.UpdateTransactionAsync(dbId.ToString(), transaction))
            : BadRequest();
    }

    [HttpDelete("{dbId:guid}/{clientId}/transactions/{transactionId:guid}")]
    public async Task<ActionResult<bool>> DeleteTransactionAsync(Guid dbId, string clientId, Guid transactionId)
    {
        bool result = await dataStore.DeleteTransactionAsync(dbId.ToString(), transactionId);
        return Ok(result);
    }
}

public class DateTimeRequest
{
    public string Date { get; set; } = string.Empty;
}