using System.Text.Json;
using System.Text.Json.Nodes;
using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Angular.Server.Services;

public interface IAccountService
{
    Task<ActionResult<Guid>> CreateTransactionAsync(Guid dbId, string clientId, Stream body, IDataStore dataStore);
}

public class AccountService : IAccountService
{
    public async Task<ActionResult<Guid>> CreateTransactionAsync(Guid dbId, string clientId, Stream body, IDataStore dataStore)
    {
        body.Seek(0, SeekOrigin.Begin);
        var jsonNode = await JsonSerializer.DeserializeAsync<JsonNode>(body);
        if (jsonNode is null)
        {
            return new BadRequestResult();
        }

        DateTime whenValue = DateTime.Parse(jsonNode["when"]?.GetValue<string>() ?? string.Empty);
        DateOnly when = DateOnly.FromDateTime(whenValue);
        string creditedAccountId = jsonNode["creditedAccountId"]?.GetValue<string>() ?? string.Empty;
        string debitedAccountId = jsonNode["debitedAccountId"]?.GetValue<string>() ?? string.Empty;
        decimal amount = decimal.Parse(jsonNode["amount"]?.GetValue<string>() ?? string.Empty);
        return new OkObjectResult(await dataStore.CreateTransactionAsync(dbId.ToString(), clientId, new Transaction(creditedAccountId, debitedAccountId, amount, when)));
    }
}
